using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

/// <summary>Shipping booking lifecycle; truck appointments remain independent aggregates.</summary>
public sealed partial class ExportBookingService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IBookingService _bookings;
    private readonly IBookingDocumentRequirementPolicy _documents;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ExportBookingService(AppDbContext db, ICurrentUser user, IBookingService bookings, IBookingDocumentRequirementPolicy documents)
    { _db = db; _user = user; _bookings = bookings; _documents = documents; }
    private bool Carrier => new[] { "Transport Company", "Carrier", "Carrier Staff" }.Contains(_user.Role, StringComparer.OrdinalIgnoreCase);
    private bool Line => string.Equals(_user.Role, "Shipping Line", StringComparison.OrdinalIgnoreCase);
    private bool Admin => new[] { "Administrator", "Admin" }.Contains(_user.Role, StringComparer.OrdinalIgnoreCase);
    private bool Port => Admin || new[] { "Dispatcher", "Gate Officer", "Gate", "Yard Operator", "Berth Staff" }.Contains(_user.Role, StringComparer.OrdinalIgnoreCase);
    private Guid Actor => _user.UserId ?? throw new UnauthorizedException("Vui lòng đăng nhập.");
    private static object[] Args(params object?[] values) => values.Select((value, index) => (object)(value == null
        ? new NpgsqlParameter("p" + index, NpgsqlDbType.Unknown) { Value = DBNull.Value }
        : new NpgsqlParameter("p" + index, value))).ToArray();
    private async Task<List<JsonObject>> Rows(string sql, CancellationToken ct, params object?[] args)
        => (await _db.Database.SqlQueryRaw<string>(sql, Args(args)).ToListAsync(ct)).Select(x => JsonNode.Parse(x)!.AsObject()).ToList();
    private Task<int> Exec(string sql, CancellationToken ct, params object?[] args) => _db.Database.ExecuteSqlRawAsync(sql, Args(args), ct);
    private static string S(JsonNode? row, string key) => row?[key]?.GetValue<string>() ?? "";
    private static Guid G(JsonNode row, string key) => Guid.Parse(S(row, key));
    private static int I(JsonNode row, string key) => row[key]?.GetValue<int>() ?? 0;
    private static DateTime D(JsonNode row, string key) => DateTime.Parse(S(row, key), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static void Require(bool condition, string key, string message) { if (!condition) throw new ValidationException(key, message); }
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json))));
    private static void Text(string value, string key, int max = 200) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= max, key, $"{key}: thông tin bắt buộc, tối đa {max} ký tự.");
    private async Task<Guid> Company(CancellationToken ct)
    {
        Require(Carrier, "Role", "Thao tác này dành cho công ty đặt Booking.");
        var ids = await _db.Database.SqlQueryRaw<Guid>("SELECT carrier_id AS \"Value\" FROM carrier_users WHERE user_id=@p0", Args(Actor)).ToListAsync(ct);
        if (ids.Count != 1) throw new UnauthorizedException("Tài khoản cần thuộc đúng một công ty vận tải.");
        return ids[0];
    }
    private async Task CanRead(JsonObject booking, CancellationToken ct)
    {
        if (Carrier && G(booking, "carrierId") == await Company(ct)) return;
        if (Line && G(booking, "shippingLineId") == Actor) return;
        if (Port) return;
        throw new NotFoundException("ExportBooking", G(booking, "id"));
    }
    private async Task Own(JsonObject booking, CancellationToken ct) { if (G(booking, "carrierId") != await Company(ct)) throw new NotFoundException("ExportBooking", G(booking, "id")); }
    private void OwnLine(JsonObject booking) { if (!(Admin || (Line && G(booking, "shippingLineId") == Actor))) throw new NotFoundException("ExportBooking", G(booking, "id")); }
    private static void Version(JsonObject row, int expected) => Require(expected > 0 && I(row, "version") == expected, "Version", "Dữ liệu đã thay đổi. Tải lại thông tin trước khi tiếp tục.");
    private const string VoyageFields = """
        v.id,v.line_user_id AS "shippingLineId",v.vessel_name AS "vesselName",v.voyage_number AS "voyageNumber",
        v.port_of_loading AS "portOfLoading",v.port_of_discharge AS "portOfDischarge",v.etd,v.eta,
        v.receive_from AS "receiveFrom",v.cy_cutoff AS "cyCutoff",v.vgm_cutoff AS "vgmCutoff",v.si_cutoff AS "siCutoff",v.status,v.version,v.departed_at AS "departedAt"
        """;
    private const string BookingFields = """
        b.id,b.booking_no AS "bookingNumber",b.carrier_id AS "carrierId",c.company_name AS "companyName",
        b.line_user_id AS "shippingLineId",COALESCE(u.full_name,u.username) AS "shippingLineName",
        b.voyage_id AS "voyageId",b.container_type_code AS "containerTypeCode",b.quantity,b.commodity_description AS "commodityDescription",
        b.status,b.version,b.port_status AS "portStatus",b.si,b.si_status AS "siStatus",b.draft_version AS "draftVersion",b.draft,
        b.bl_number AS "blNumber",b.freight_paid AS "freightPaid",b.created_at AS "createdAt",b.created_by AS "createdBy",b.submission
        """;
    private async Task<JsonObject> Booking(Guid id, bool locked, CancellationToken ct)
    {
        var rows = await Rows("SELECT row_to_json(x)::text AS \"Value\" FROM (SELECT " + BookingFields +
            " FROM export_bookings b JOIN carriers c ON c.id=b.carrier_id JOIN users u ON u.id=b.line_user_id WHERE b.id=@p0" + (locked ? " FOR UPDATE OF b" : "") + ") x", ct, id);
        if (rows.Count != 1) throw new NotFoundException("ExportBooking", id);
        return rows[0];
    }
    private async Task<JsonObject> Voyage(Guid id, bool locked, CancellationToken ct)
    {
        var rows = await Rows("SELECT row_to_json(x)::text AS \"Value\" FROM (SELECT " + VoyageFields + " FROM export_voyages v WHERE v.id=@p0" + (locked ? " FOR UPDATE" : "") + ") x", ct, id);
        if (rows.Count != 1) throw new NotFoundException("ExportVoyage", id);
        return rows[0];
    }
    public async Task<JsonObject> Detail(Guid id, CancellationToken ct)
    {
        var b = await Booking(id, false, ct); await CanRead(b, ct);
        b["voyage"] = await Voyage(G(b, "voyageId"), false, ct);
        b["containers"] = new JsonArray((await Containers(id, ct)).Select(x => (JsonNode)x).ToArray());
        b["history"] = new JsonArray((await Rows("""
            SELECT row_to_json(x)::text AS "Value" FROM (SELECT id,event,actor_id AS "actorId",description,created_at AS "createdAt"
            FROM export_history WHERE export_booking_id=@p0 ORDER BY created_at,id) x
            """, ct, id)).Select(x => (JsonNode)x).ToArray());
        CustomerSubmissionVisibility(b);
        return b;
    }
    private void CustomerSubmissionVisibility(JsonObject booking)
    {
        // The transferable pickup code is needed only by the customer and the server handoff.
        if (!Carrier && booking["submission"]?["pickup"] is JsonObject pickup) pickup.Remove("yardCode");
        if (!Carrier && booking["submission"]?["pickupSource"] is JsonObject source) source.Remove("yardCode");
    }
    private Task<List<JsonObject>> Containers(Guid id, CancellationToken ct) => Rows("""
        SELECT row_to_json(x)::text AS "Value" FROM (
        SELECT e.id,e.container_id AS "containerId",e.declaration_id AS "declarationId",e.container_no AS "containerNumber",e.type_code AS "containerTypeCode",
        e.seal_number AS "sealNumber",e.vgm_kg AS "vgmKg",e.cargo_type AS "cargoType",e.target_temperature AS "targetTemperature",
        e.un_number AS "unNumber",e.dg_class AS "dgClass",e.customs_document_url AS "customsDocumentUrl",e.customs_cleared AS "customsCleared",
        e.status,e.gate_in_at AS "gateInAt",e.loaded_at AS "loadedAt",e.exclusion_reason AS "exclusionReason",
        EXISTS(SELECT 1 FROM export_gate_links h WHERE h.export_container_id=e.id) AS "hasGateHistory",
        (SELECT l.gate_booking_id FROM export_gate_links l JOIN bookings g ON g.id=l.gate_booking_id WHERE l.export_container_id=e.id AND g.status::text NOT IN ('canceled','rejected','expired') ORDER BY l.created_at DESC LIMIT 1) AS "gateBookingId"
        FROM export_booking_containers e WHERE export_booking_id=@p0 ORDER BY e.container_no) x
        """, ct, id);
    public async Task<JsonObject> Options(CancellationToken ct)
    {
        var result = new JsonObject();
        result["shippingLines"] = new JsonArray((await Rows("SELECT row_to_json(x)::text AS \"Value\" FROM (SELECT id,COALESCE(full_name,username) AS name FROM users WHERE role='Shipping Line' AND is_active ORDER BY username) x", ct)).Select(x => (JsonNode)x).ToArray());
        result["containerTypes"] = new JsonArray((await Rows("SELECT row_to_json(x)::text AS \"Value\" FROM (SELECT code,code AS name FROM container_types ORDER BY code) x", ct)).Select(x => (JsonNode)x).ToArray());
        result["voyages"] = new JsonArray((await Voyages(ct)).Where(x => S(x,"status")=="Scheduled" && D(x,"vgmCutoff")>DateTime.UtcNow).Select(x => (JsonNode)x).ToArray());
        result["sources"] = new JsonArray();
        if (Carrier)
        {
            var carrier = await Company(ct);
            result["companyName"] = (await Rows("SELECT jsonb_build_object('name',company_name)::text AS \"Value\" FROM carriers WHERE id=@p0", ct, carrier))[0]["name"]!.DeepClone();
            result["sources"] = new JsonArray((await Rows("""
                SELECT row_to_json(x)::text AS "Value" FROM (SELECT c.id AS "containerId",d.id AS "declarationId",c.container_no AS "containerNumber",
                t.code AS "containerTypeCode",d.seal_number AS "sealNumber",d.gross_weight_kg AS "grossWeightKg",d.cargo_type::text AS "cargoType",d.required_power_kw AS "requiredPowerKw",d.customer_name AS "customerName"
                FROM container_declarations d JOIN containers c ON c.id=d.container_id JOIN container_types t ON t.id=c.container_type_id
                WHERE d.carrier_id=@p0 AND d.load_status='full'
                AND NOT EXISTS(SELECT 1 FROM export_booking_containers e WHERE e.container_id=c.id AND e.active)
                AND NOT EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id WHERE bc.declaration_id=d.id AND b.status::text NOT IN ('canceled','rejected','expired'))
                AND NOT EXISTS(SELECT 1 FROM container_visits cv WHERE cv.container_id=c.id AND cv.visit_status IN ('planned','active'))
                ORDER BY c.container_no,d.created_at DESC LIMIT 200) x
                """, ct, carrier)).Select(x => (JsonNode)x).ToArray());
        }
        return result;
    }
    public async Task<List<JsonObject>> Voyages(CancellationToken ct)
    {
        var filter = Line ? " WHERE v.line_user_id=@p0" : "";
        return await Rows("SELECT row_to_json(x)::text AS \"Value\" FROM (SELECT " + VoyageFields + " FROM export_voyages v" + filter + " ORDER BY v.etd DESC LIMIT 500) x", ct, Line ? new object?[] { Actor } : Array.Empty<object?>());
    }
    public async Task<JsonObject> List(int page, int pageSize, string? search, string? status, Guid? voyageId, CancellationToken ct)
    {
        page = Math.Clamp(page,1,1000000); pageSize = Math.Clamp(pageSize,1,100);
        Guid? carrier = Carrier ? await Company(ct) : null;
        Require(Carrier || Line || Port,"Role","Không có quyền xem Booking xuất khẩu.");
        const string where="""
             FROM export_bookings b JOIN carriers c ON c.id=b.carrier_id JOIN users u ON u.id=b.line_user_id
             WHERE (@p0::uuid IS NULL OR b.carrier_id=@p0) AND (@p1::uuid IS NULL OR b.line_user_id=@p1)
             AND (@p2::text IS NULL OR b.booking_no ILIKE '%'||@p2||'%' OR c.company_name ILIKE '%'||@p2||'%')
             AND (@p3::text IS NULL OR b.status=@p3) AND (@p4::uuid IS NULL OR b.voyage_id=@p4)
            """;
        object?[] args={carrier,Line?Actor:null,string.IsNullOrWhiteSpace(search)?null:search[..Math.Min(100,search.Length)],string.IsNullOrWhiteSpace(status)?null:status,voyageId};
        var count=await Rows("SELECT jsonb_build_object('count',count(*)::int)::text AS \"Value\" "+where,ct,args);
        var rows = await Rows("SELECT row_to_json(x)::text AS \"Value\" FROM (SELECT " + BookingFields +
             ",(SELECT row_to_json(voy) FROM (SELECT "+VoyageFields+" FROM export_voyages v WHERE v.id=b.voyage_id) voy) AS voyage,"+
             "(SELECT COALESCE(jsonb_agg(jsonb_build_object('id',e.id,'containerId',e.container_id,'containerNumber',e.container_no,'status',e.status)), '[]'::jsonb) FROM export_booking_containers e WHERE e.export_booking_id=b.id) AS containers "+
             where+" ORDER BY b.created_at DESC LIMIT @p5 OFFSET @p6) x",ct,args.Concat(new object?[]{pageSize,(page-1)*pageSize}).ToArray());
        foreach (var row in rows) CustomerSubmissionVisibility(row);
        return new JsonObject { ["items"] = new JsonArray(rows.Select(x=>(JsonNode)x).ToArray()), ["totalCount"]=I(count[0],"count"),["page"]=page,["pageSize"]=pageSize };
    }
    private async Task History(Guid id, Guid voyageId, string kind, string description, CancellationToken ct)
        => await Exec("INSERT INTO export_history(id,export_booking_id,voyage_id,event,actor_id,description) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",ct,Guid.NewGuid(),id,voyageId,kind,Actor,description);
    // Same-application port adapter persists the accepted receipt in the same transaction.
    // The outbox records actual receipt, not an assertion that an external port was contacted.
    private async Task Deliver(Guid id, Guid voyageId, int version, string kind, object payload, CancellationToken ct)
    {
        var messageId=Guid.NewGuid(); var body=JsonSerializer.Serialize(payload,Json);
        await Exec("INSERT INTO export_outbox(id,export_booking_id,voyage_id,version,event,payload) VALUES(@p0,@p1,@p2,@p3,@p4,@p5::jsonb)",ct,messageId,id,voyageId,version,kind,body);
        await Exec("INSERT INTO export_inbox(event_id,export_booking_id,voyage_id,version,event,payload_hash) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",ct,messageId,id,voyageId,version,kind,Hash(payload));
        await Exec("UPDATE export_outbox SET delivered_at=now() WHERE id=@p0",ct,messageId);
    }
    public async Task<JsonObject> Create(CreateExportBookingRequest request,CancellationToken ct)
    {
        var company=await Company(ct);
        Require(request.RequestId!=Guid.Empty,"RequestId","Thiếu mã yêu cầu chống tạo trùng.");
        Text(request.ContainerTypeCode,"Loại container",30);Text(request.CommodityDescription,"Mô tả hàng",2000);
        Require(request.Quantity is >=1 and <=50,"Quantity","Số lượng container từ 1 đến 50.");
        var fingerprint=Hash(request);
        await using var transaction=await _db.Database.BeginTransactionAsync(ct);
        await Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))",ct,"export-create:"+company+":"+request.RequestId);
        var replay=await Rows("SELECT jsonb_build_object('id',id,'hash',request_hash)::text AS \"Value\" FROM export_bookings WHERE carrier_id=@p0 AND request_id=@p1",ct,company,request.RequestId);
        if(replay.Count>0){Require(S(replay[0],"hash")==fingerprint,"RequestId","Mã yêu cầu đã được dùng với nội dung khác.");await transaction.CommitAsync(ct);return await Detail(G(replay[0],"id"),ct);}
        var voyage=await Voyage(request.VoyageId,true,ct);
        Require(G(voyage,"shippingLineId")==request.ShippingLineId && S(voyage,"status")=="Scheduled" && D(voyage,"vgmCutoff")>DateTime.UtcNow,"VoyageId","Chọn chuyến tàu đang mở của đúng hãng tàu.");
        var line=await Rows("SELECT jsonb_build_object('id',id)::text AS \"Value\" FROM users WHERE id=@p0 AND role='Shipping Line' AND is_active",ct,request.ShippingLineId);
        Require(line.Count==1,"ShippingLineId","Hãng tàu không còn hoạt động.");
        var type=await Rows("SELECT jsonb_build_object('code',code)::text AS \"Value\" FROM container_types WHERE code=@p0",ct,request.ContainerTypeCode.Trim().ToUpperInvariant());
        Require(type.Count==1,"ContainerTypeCode","Loại container không tồn tại.");
        var id=Guid.NewGuid();
        await Exec("""
            INSERT INTO export_bookings(id,request_id,request_hash,carrier_id,line_user_id,voyage_id,booking_no,container_type_code,quantity,commodity_description,created_by)
            VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10)
            """,ct,id,request.RequestId,fingerprint,company,request.ShippingLineId,request.VoyageId,"EXP-"+id.ToString("N").ToUpperInvariant(),request.ContainerTypeCode.Trim().ToUpperInvariant(),request.Quantity,request.CommodityDescription.Trim(),Actor);
        await History(id,request.VoyageId,"BookingRequested","Khách hàng gửi yêu cầu đặt chỗ; chưa có danh sách container.",ct);
        await transaction.CommitAsync(ct);return await Detail(id,ct);
    }
    public async Task<JsonObject> CreateVoyage(CreateExportVoyageRequest request,CancellationToken ct)
    {
        Require(Line||Admin,"Role","Chỉ hãng tàu được thiết lập chuyến tàu.");
        var line=Line?Actor:request.ShippingLineId;
        Text(request.VesselName,"Tên tàu");Text(request.VoyageNumber,"Số chuyến",100);Text(request.PortOfLoading,"Cảng xếp");Text(request.PortOfDischarge,"Cảng dỡ");
        Require(request.Etd.Kind==DateTimeKind.Utc&&request.Eta.Kind==DateTimeKind.Utc&&request.ReceiveFrom.Kind==DateTimeKind.Utc&&request.CyCutoff.Kind==DateTimeKind.Utc&&request.VgmCutoff.Kind==DateTimeKind.Utc&&request.SiCutoff.Kind==DateTimeKind.Utc,"Time","Gửi các mốc thời gian có múi giờ UTC.");
        Require(request.ReceiveFrom<request.CyCutoff&&request.CyCutoff<request.Etd&&request.VgmCutoff<request.Etd&&request.SiCutoff<request.Etd&&request.Eta>request.Etd&&request.VgmCutoff>DateTime.UtcNow,"Cutoff","Các mốc nhận hàng/cut-off/ETD/ETA không hợp lệ.");
        Require((await Rows("SELECT jsonb_build_object('id',id)::text AS \"Value\" FROM users WHERE id=@p0 AND role='Shipping Line' AND is_active",ct,line)).Count==1,"ShippingLineId","Chọn tài khoản hãng tàu đang hoạt động.");
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        await Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))",ct,"voyage:"+line+":"+request.VoyageNumber.Trim());
        Require((await Rows("SELECT jsonb_build_object('id',id)::text AS \"Value\" FROM export_voyages WHERE line_user_id=@p0 AND voyage_number=@p1",ct,line,request.VoyageNumber.Trim())).Count==0,"VoyageNumber","Số chuyến đã tồn tại trong hãng tàu này.");
        var id=Guid.NewGuid();
        await Exec("""
            INSERT INTO export_voyages(id,line_user_id,vessel_name,voyage_number,port_of_loading,port_of_discharge,etd,eta,receive_from,cy_cutoff,vgm_cutoff,si_cutoff)
            VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11)
            """,ct,id,line,request.VesselName.Trim(),request.VoyageNumber.Trim(),request.PortOfLoading.Trim(),request.PortOfDischarge.Trim(),request.Etd,request.Eta,request.ReceiveFrom,request.CyCutoff,request.VgmCutoff,request.SiCutoff);
        await tx.CommitAsync(ct);return await Voyage(id,false,ct);
    }
}

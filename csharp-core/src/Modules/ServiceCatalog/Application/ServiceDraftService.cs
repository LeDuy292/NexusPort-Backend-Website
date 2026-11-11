using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

/// <summary>Company-private drafts and service confirmations. Source workflows remain read-only.</summary>
public sealed partial class ServiceDraftService(AppDbContext db, ICurrentUser user, IServiceWorkflowEmail? workflowEmail = null, IHostEnvironment? environment = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static object[] Args(params object?[] args) => args.Select((value, i) => (object)(value == null
        ? new NpgsqlParameter("p" + i, NpgsqlDbType.Unknown) { Value = DBNull.Value }
        : new NpgsqlParameter("p" + i, value))).ToArray();
    private async Task<List<JsonObject>> Rows(string sql, CancellationToken ct, params object?[] args)
        => (await db.Database.SqlQueryRaw<string>("SELECT row_to_json(q)::text AS \"Value\" FROM (" + sql + ") q", Args(args)).ToListAsync(ct))
            .Select(value => JsonNode.Parse(value)!.AsObject()).ToList();
    private Task<int> Exec(string sql, CancellationToken ct, params object?[] args) => db.Database.ExecuteSqlRawAsync(sql, Args(args), ct);
    private Guid Actor => user.UserId ?? throw new CatalogException("Vui lòng đăng nhập.", 401, "UNAUTHORIZED");
    private static string S(JsonNode? row, string key) => row?[key]?.GetValue<string>() ?? "";
    private static Guid G(JsonNode row, string key) => Guid.Parse(S(row, key));
    private static void Require(bool valid, string message, int status = 422, string code = "SERVICE_DRAFT_VALIDATION")
        => ServiceDraftRules.Require(valid, message, status, code);

    private async Task<Guid> Company(CancellationToken ct)
    {
        var accounts = await Rows("SELECT role FROM users WHERE id=@p0 AND is_active", ct, Actor);
        var role = accounts.FirstOrDefault()?["role"]?.GetValue<string>();
        Require(role != null && string.Equals(role, user.Role, StringComparison.OrdinalIgnoreCase)
            && role is "Transport Company" or "Carrier" or "Carrier Staff", "Tài khoản không có quyền tạo hồ sơ dịch vụ.", 403, "FORBIDDEN");
        var companies = await Rows("SELECT carrier_id AS \"carrierId\" FROM carrier_users WHERE user_id=@p0", ct, Actor);
        Require(companies.Count == 1, "Tài khoản cần thuộc đúng một công ty vận tải.", 403, "SERVICE_DRAFT_MEMBERSHIP");
        return G(companies[0], "carrierId");
    }

    private const string ExportEligible = """
        b.port_status='Accepted' AND b.status IN ('ContainersDeclared','GateIn')
        AND v.status='Scheduled' AND e.status='Expected' AND e.active AND d.load_status='full'
        AND v.cy_cutoff>now() AND v.receive_from IS NOT NULL
        AND e.vgm_kg>0 AND NULLIF(trim(e.seal_number),'') IS NOT NULL
        AND regexp_replace(lower(COALESCE(to_jsonb(c)->>'status','')),'[^a-z0-9]','','g')
            NOT IN ('hold','customshold','inactive','blocked','damagedunsafe','cancelled','archived')
        """;
    private const string ExportJoins = """
        FROM export_bookings b JOIN export_voyages v ON v.id=b.voyage_id
        JOIN export_booking_containers e ON e.export_booking_id=b.id
        JOIN container_declarations d ON d.id=e.declaration_id AND d.container_id=e.container_id AND d.carrier_id=b.carrier_id
        JOIN containers c ON c.id=e.container_id
        """;

    private async Task EnsureExportSourceSchema(CancellationToken ct)
    {
        // A version receipt alone cannot guarantee an upstream table was not
        // subsequently changed. Never infer company rights from shipper names
        // or the creator's current membership when Booking ownership is absent.
        var columns = await Rows("""
            SELECT column_name AS name FROM information_schema.columns
            WHERE table_schema=current_schema() AND table_name='export_bookings'
            """, ct);
        var available = columns.Select(column => S(column, "name")).ToHashSet();
        Require(new[] { "id", "carrier_id", "booking_no", "voyage_id", "status", "port_status", "created_at" }.All(available.Contains),
            "Nguồn Booking hãng tàu chưa sẵn sàng để đăng ký dịch vụ. Vui lòng liên hệ quản trị viên kiểm tra liên kết Booking với công ty.",
            503, "SERVICE_SOURCE_SCHEMA_MISMATCH");
    }

    private async Task<JsonObject> ExportSource(Guid company, Guid id, CancellationToken ct)
    {
        await EnsureExportSourceSchema(ct);
        var owned = await Rows("SELECT id FROM export_bookings WHERE id=@p0 AND carrier_id=@p1", ct, id, company);
        Require(owned.Count == 1, "Không tìm thấy Booking trong công ty của bạn.", 404, "SERVICE_SOURCE_NOT_FOUND");
        var rows = await Rows("""
            SELECT b.id,b.booking_no AS "bookingNumber",b.status,b.port_status AS "portStatus",
                v.vessel_name AS "vesselName",v.voyage_number AS "voyageNumber",
                v.receive_from AS "receiveFrom",v.cy_cutoff AS "cyCutoff",count(e.id)::int AS "containerCount"
            """ + ExportJoins + " WHERE b.id=@p0 AND b.carrier_id=@p1 AND " + ExportEligible + """
             GROUP BY b.id,b.booking_no,b.status,b.port_status,v.vessel_name,v.voyage_number,v.receive_from,v.cy_cutoff
            """, ct, id, company);
        Require(rows.Count == 1, "Booking chưa có danh sách container hàng được công bố để đăng ký dịch vụ.");
        rows[0]["verificationStatus"] = "SOURCE_LINKED";
        return rows[0];
    }

    public async Task<object> Sources(string flow, string? search, CancellationToken ct)
    {
        var company = await Company(ct);
        flow = ServiceDraftRules.Flow(flow);
        var term = ServiceDraftRules.Search(search);
        if (flow == "IMPORT") return new
        {
            flow, referenceMode = "MANUAL", items = Array.Empty<object>(),
            notice = "Khai báo tham chiếu DO/eDO để lưu nháp. Thông tin này đang chờ đối chiếu với nguồn cấp lệnh."
        };
        await EnsureExportSourceSchema(ct);
        var rows = await Rows("""
            SELECT b.id,b.booking_no AS "bookingNumber",v.vessel_name AS "vesselName",v.voyage_number AS "voyageNumber",
                v.receive_from AS "receiveFrom",v.cy_cutoff AS "cyCutoff",count(e.id)::int AS "containerCount"
            """ + ExportJoins + " WHERE b.carrier_id=@p0 AND " + ExportEligible + """
             AND (b.booking_no ILIKE @p1 OR v.vessel_name ILIKE @p1 OR v.voyage_number ILIKE @p1)
             GROUP BY b.id,b.booking_no,v.vessel_name,v.voyage_number,v.receive_from,v.cy_cutoff,b.created_at
             ORDER BY b.created_at DESC,b.id LIMIT 50
            """, ct, company, "%" + term + "%");
        return new { flow, referenceMode = "BOOKING", items = rows };
    }

    private async Task<List<JsonObject>> ContainerRows(Guid company, string flow, Guid? sourceId, string search, Guid[]? ids, CancellationToken ct)
    {
        // Bounded picker searches and exact-ID save validation share the same scope.
        // An exact save selection is not constrained by the first picker page.
        if (flow == "EXPORT")
        {
            Require(sourceId.HasValue, "Chọn Booking trước khi chọn container.");
            await ExportSource(company, sourceId!.Value, ct);
            var filter = ids == null ? " AND e.container_no ILIKE @p2" : " AND e.id=ANY(@p2)";
            return await Rows("""
                SELECT e.id AS "sourceContainerId",e.container_id AS "containerId",e.container_no AS "containerNumber",
                    'EXPORT'::text AS flow,b.id AS "bookingId",
                    e.type_code AS "typeCode",'full'::text AS "loadStatus",e.cargo_type::text AS "cargoType",
                    e.seal_number AS "sealNumber",e.vgm_kg AS "vgmKg",e.target_temperature AS "targetTemperature",e.un_number AS "unNumber",e.dg_class AS "dgClass"
                """ + ExportJoins + " WHERE b.id=@p0 AND b.carrier_id=@p1 AND " + ExportEligible + filter +
                " ORDER BY e.container_no,e.id LIMIT 50", ct, sourceId.Value, company, ids == null ? "%" + search + "%" : ids);
        }
        var importFilter = ids == null ? " AND c.container_no ILIKE @p1" : " AND v.id=ANY(@p1)";
        return await Rows("""
            SELECT v.id AS "sourceContainerId",c.id AS "containerId",c.container_no AS "containerNumber",t.code AS "typeCode",
                'IMPORT'::text AS flow,v.movement_type AS "movementType",v.visit_status AS "visitStatus",
                'full'::text AS "loadStatus",
                CASE WHEN d.carrier_id=@p0 THEN d.cargo_type::text ELSE NULL END AS "cargoType",
                CASE WHEN d.carrier_id=@p0 THEN d.seal_number ELSE NULL END AS "sealNumber",
                NULL::numeric AS "vgmKg",NULL::numeric AS "targetTemperature"
            FROM container_visits v JOIN containers c ON c.id=v.container_id
            JOIN container_types t ON t.id=c.container_type_id
            JOIN container_declarations d ON d.id=v.declaration_id AND d.container_id=v.container_id
            WHERE v.load_status='full' AND d.load_status='full' AND v.visit_status='active'
                AND v.movement_type='vessel_discharge' AND v.data_status='verified'
                AND (to_jsonb(v)->>'source_type' IS NULL OR to_jsonb(v)->>'source_type'='port_vessel')
                AND v.started_at IS NOT NULL AND v.completed_at IS NULL
                AND (v.valid_to IS NULL OR v.valid_to>=now())
                AND (to_jsonb(v)->>'expected_available_at' IS NULL OR (to_jsonb(v)->>'expected_available_at')::timestamptz<=now())
                AND regexp_replace(lower(COALESCE(to_jsonb(c)->>'status','')),'[^a-z0-9]','','g')
                    NOT IN ('hold','customshold','inactive','blocked','damagedunsafe','cancelled','archived','gateout','departed','outside','outofyard')
                AND (d.carrier_id=@p0 OR EXISTS(SELECT 1 FROM container_visit_access a WHERE a.container_visit_id=v.id AND a.carrier_id=@p0))
            """ + importFilter + " ORDER BY c.container_no,v.id LIMIT 50", ct, company, ids == null ? "%" + search + "%" : ids);
    }

    public async Task<object> Containers(string flow, Guid? sourceId, string? search, CancellationToken ct)
    {
        var company = await Company(ct);
        flow = ServiceDraftRules.Flow(flow);
        Require(flow != "IMPORT" || sourceId == null, "Luồng nhập không dùng Booking xuất.");
        var items = await ContainerRows(company, flow, sourceId, ServiceDraftRules.Search(search), null, ct);
        return new { flow, sourceId, items, limit = 50 };
    }

    private const string DraftFields = """
        d.id,d.carrier_id AS "carrierId",d.number,d.flow,d.main_service_id AS "mainServiceId",d.status,d.version,
        d.content,d.source_snapshot AS "sourceSnapshot",d.container_snapshots AS "containers",
        d.created_at AS "createdAt",d.updated_at AS "updatedAt"
        """;
    private async Task<JsonObject?> Find(Guid id, CancellationToken ct, bool locked = false)
        => (await Rows("SELECT " + DraftFields + " FROM eport_service_drafts d WHERE d.id=@p0" + (locked ? " FOR UPDATE OF d" : ""), ct, id)).SingleOrDefault();

    private static JsonObject Detail(JsonObject row)
    {
        var result = row["content"]!.DeepClone().AsObject();
        foreach (var key in new[] { "id", "number", "status", "version", "createdAt", "updatedAt", "sourceSnapshot", "containers" })
            result[key] = row[key]?.DeepClone();
        return result;
    }

    public async Task<JsonObject> Get(Guid id, CancellationToken ct)
    {
        var company = await Company(ct);
        var row = await Find(id, ct);
        Require(row != null && G(row, "carrierId") == company, "Không tìm thấy bản nháp dịch vụ.", 404, "SERVICE_DRAFT_NOT_FOUND");
        var detail = Detail(row!);
        var registration = await FindRegistration(id, ct);
        if (registration != null) AddRegistrationMetadata(detail, registration);
        await ApplyWorkflow(detail, id, ct);
        return detail;
    }

    public async Task<object> List(string? search, int page, int pageSize, CancellationToken ct)
    {
        var company = await Company(ct);
        var term = ServiceDraftRules.Search(search);
        Require(page is >= 1 and <= 100000 && pageSize is >= 1 and <= 50, "Phân trang không hợp lệ, tối đa 50 bản nháp mỗi trang.");
        const string filter = """
            FROM eport_service_drafts d LEFT JOIN eport_services s ON s.id=d.main_service_id
            LEFT JOIN eport_service_registrations r ON r.draft_id=d.id
            WHERE d.carrier_id=@p0 AND (d.number ILIKE @p1 OR COALESCE(r.number,'') ILIKE @p1
              OR COALESCE(r.snapshot->>'mainServiceName',s.name,'') ILIKE @p1
              OR COALESCE(r.snapshot->'sourceSnapshot'->>'bookingNumber',d.source_snapshot->>'bookingNumber','') ILIKE @p1
              OR COALESCE(d.content->'source'->>'referenceNumber','') ILIKE @p1)
            """;
        var count = (await Rows("SELECT count(*) AS total " + filter, ct, company, "%" + term + "%"))[0]["total"]!.GetValue<long>();
        var rows = await Rows("""
            SELECT d.id,d.number,d.flow,COALESCE(r.status,d.status) AS status,d.version,
                COALESCE(r.snapshot->>'mainServiceName',s.name) AS "mainServiceName",
                r.id AS "registrationId",r.number AS "registrationNumber",r.total,r.currency,r.payment_status AS "paymentStatus",
                COALESCE(r.snapshot->'sourceSnapshot'->>'bookingNumber',r.snapshot->'content'->'source'->>'referenceNumber',
                    d.source_snapshot->>'bookingNumber',NULLIF(d.content->'source'->>'referenceNumber',''),'Chưa chọn hồ sơ') AS "sourceLabel",
                jsonb_array_length(d.container_snapshots) AS "containerCount",COALESCE(r.confirmed_at,d.updated_at) AS "updatedAt"
            """ + filter + " ORDER BY COALESCE(r.confirmed_at,d.updated_at) DESC,d.id LIMIT @p2 OFFSET @p3", ct, company, "%" + term + "%", pageSize, (page - 1) * pageSize);
        await ApplyWorkflowRows(rows, ct);
        return new { items = rows, page, pageSize, total = count };
    }

    private static JsonObject Content(ServiceDraftInput input) => JsonSerializer.SerializeToNode(new
    {
        input.Flow, input.MainServiceId, input.Source, input.PlannedAt,
        input.ContactName, input.ContactPhone, input.Note, input.Lines, input.WorkflowVersion, input.Delivery, input.InvoiceBuyer
    }, Json)!.AsObject();

    private static bool ClearlyNonReefer(JsonObject container)
    {
        var code = S(container, "typeCode").ToUpperInvariant();
        var cargo = S(container, "cargoType").ToLowerInvariant();
        if ((code.Length > 2 && code[2] == 'R') || code.Contains("RF") || code.Contains("RH")
            || cargo.Contains("reefer") || cargo.Contains("frozen") || cargo.Contains("lạnh")) return false;
        return (code.Length > 2 && code[2] is 'G' or 'P' or 'T' or 'U')
            || code.Contains("DC") || code.Contains("DR") || code.Contains("DRY") || code.Contains("GP")
            || code.Contains("HC") || code.Contains("OT") || code.Contains("FR");
    }

    private async Task<(JsonObject Source, JsonArray Containers)> Snapshots(Guid company, ServiceDraftInput input, CancellationToken ct)
    {
        JsonObject source;
        if (input.Flow == "EXPORT")
            source = input.Source.BookingId.HasValue
                ? await ExportSource(company, input.Source.BookingId.Value, ct)
                : new JsonObject { ["verificationStatus"] = "PENDING_SOURCE" };
        else source = new JsonObject
        {
            ["documentType"] = input.Source.DocumentType,
            ["referenceNumber"] = input.Source.ReferenceNumber,
            ["verificationStatus"] = "UNVERIFIED",
            ["notice"] = "Tham chiếu DO/eDO đang chờ đối chiếu; bản nháp chưa xác nhận quyền nhận hàng."
        };
        var requested = input.Lines.Select(line => line.SourceContainerId).ToArray();
        var containers = requested.Length == 0 ? [] : await ContainerRows(company, input.Flow, input.Source.BookingId, "", requested, ct);
        Require(containers.Count == requested.Length, "Một hoặc nhiều container không còn thuộc nguồn được phép chọn của công ty.");
        var serviceIds = input.Lines.SelectMany(line => line.Services).Select(usage => usage.ServiceId)
            .Concat(input.MainServiceId.HasValue ? [input.MainServiceId.Value] : Array.Empty<Guid>()).Distinct().ToArray();
        var services = serviceIds.Length == 0 ? [] : await Rows("SELECT id,code,name,direction,unit,is_active AS \"isActive\" FROM eport_services WHERE id=ANY(@p0)", ct, serviceIds);
        Require(services.Count == serviceIds.Length && services.All(service => service["isActive"]!.GetValue<bool>()), "Dịch vụ không tồn tại hoặc đã tạm ngừng cung cấp.");
        Require(services.All(service => S(service, "direction") is "BOTH" || S(service, "direction") == input.Flow), "Dịch vụ không phù hợp với luồng hàng đã chọn.");
        var byService = services.ToDictionary(service => G(service, "id"));
        var byContainer = containers.ToDictionary(container => G(container, "sourceContainerId"));
        var snapshots = new JsonArray();
        foreach (var line in input.Lines)
        {
            var container = byContainer[line.SourceContainerId];
            foreach (var usage in line.Services)
            {
                var service = byService[usage.ServiceId];
                Require(usage.ServiceId == input.MainServiceId || S(service, "code") is not ("EXPORT_DROPOFF" or "IMPORT_PICKUP"), "Dịch vụ giao nhận chỉ được chọn làm dịch vụ chính.");
                Require(usage.Quantity == null || S(service, "unit") == "HOUR" || usage.Quantity == decimal.Truncate(usage.Quantity.Value), "Chỉ dịch vụ tính theo giờ được nhập số lượng thập phân.");
                Require(S(service, "code") != "REEFER_POWER" || !ClearlyNonReefer(container), "Dịch vụ cắm điện chỉ dành cho container lạnh.");
            }
            // The server snapshot contains only fields already permitted by the
            // source query. No client labels, company IDs or cargo are trusted.
            snapshots.Add(container.DeepClone());
        }
        return (source, snapshots);
    }

    public async Task<JsonObject> Save(Guid id, ServiceDraftInput request, CancellationToken ct)
    {
        Require(id != Guid.Empty, "Mã bản nháp không hợp lệ.");
        var company = await Company(ct);
        var input = await DeliveryDefaults(ServiceDraftRules.Normalize(request), ct);
        var content = Content(input);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))", ct, "eport-service-draft:" + id.ToString("N"));
        await Exec("SELECT set_config('nexusport.service_actor',@p0,true)", ct, Actor.ToString());
        var existing = await Find(id, ct, true);
        if (existing != null)
        {
            Require(G(existing, "carrierId") == company, "Không tìm thấy bản nháp dịch vụ.", 404, "SERVICE_DRAFT_NOT_FOUND");
            await EnsureDraftWorkflowOpen(id, ct);
            Require(await FindRegistration(id, ct) == null, "Hồ sơ đã xác nhận và được khóa đơn giá; không thể ghi đè bản nháp.", 409, "SERVICE_REGISTRATION_LOCKED");
            if (input.ExpectedVersion == 0 && JsonNode.DeepEquals(existing["content"], content))
            {
                await tx.CommitAsync(ct);
                return Detail(existing);
            }
            Require(input.ExpectedVersion == existing["version"]!.GetValue<int>(), "Bản nháp vừa được cập nhật. Tải lại trước khi lưu.", 409, "SERVICE_DRAFT_VERSION_CONFLICT");
        }
        else Require(input.ExpectedVersion == 0, "Không tìm thấy bản nháp dịch vụ.", 404, "SERVICE_DRAFT_NOT_FOUND");
        var snapshot = await Snapshots(company, input, ct);
        if (existing == null)
            await Exec("""
                INSERT INTO eport_service_drafts(id,carrier_id,created_by,number,flow,main_service_id,content,source_snapshot,container_snapshots)
                VALUES(@p0,@p1,@p2,@p3,@p4,@p5,CAST(@p6 AS jsonb),CAST(@p7 AS jsonb),CAST(@p8 AS jsonb))
                """, ct, id, company, Actor, "DVN-" + id.ToString("N").ToUpperInvariant(), input.Flow, input.MainServiceId,
                content.ToJsonString(), snapshot.Source.ToJsonString(), snapshot.Containers.ToJsonString());
        else
            await Exec("""
                UPDATE eport_service_drafts SET flow=@p1,main_service_id=@p2,content=CAST(@p3 AS jsonb),
                    source_snapshot=CAST(@p4 AS jsonb),container_snapshots=CAST(@p5 AS jsonb),version=version+1,updated_at=now()
                WHERE id=@p0
                """, ct, id, input.Flow, input.MainServiceId, content.ToJsonString(), snapshot.Source.ToJsonString(), snapshot.Containers.ToJsonString());
        var saved = await Find(id, ct);
        await tx.CommitAsync(ct);
        return Detail(saved!);
    }
}

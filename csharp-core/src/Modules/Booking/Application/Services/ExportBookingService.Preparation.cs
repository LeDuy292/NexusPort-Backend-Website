using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public sealed partial class ExportBookingService
{
    private const string DraftSelect = """
        SELECT id,content,version,created_at AS "createdAt",updated_at AS "updatedAt",submitted_booking_id AS "submittedBookingId",
          last_request_id AS "lastRequestId",content_hash AS hash FROM export_booking_drafts
        """;
    private static void PreparationShape(ExportPreparationContent content)
    {
        Require(content != null && content.SchemaVersion == 1 && content.Mode is "containers" or "capacity", "Content", "Phiên bản hoặc hình thức hồ sơ không hợp lệ.");
        Require(content!.Description?.Length is null or <= 1000, "Description", "Ghi chú tối đa 1000 ký tự.");
        Require(content.Containers is { Count: <= 50 } && content.Containers.All(c => c != null) &&
            content.EquipmentRequests is { Count: <= 20 } && content.EquipmentRequests.All(c => c != null) &&
            content.SharedDocuments is { Count: <= 30 }, "Content", "Hồ sơ tối đa 50 container, 20 loại thiết bị và 30 chứng từ chung.");
        Require(content.Containers.Select(c => c.ContainerId).Distinct().Count() == content.Containers.Count &&
            content.Containers.Select(c => c.DeclarationId).Distinct().Count() == content.Containers.Count, "Containers", "Mỗi container và bản khai chỉ chọn một lần.");
        foreach (var c in content.Containers)
            Require(c.ContainerId != Guid.Empty && c.DeclarationId != Guid.Empty && (c.SealNumber?.Length ?? 0) <= 100 && (c.UnNumber?.Length ?? 0) <= 30 && (c.DgClass?.Length ?? 0) <= 30 &&
                c.Documents is { Count: <= 30 } && (c.VgmKg is null or >= 0 and <= 99999999), "Containers", "Thông tin container hoặc chứng từ quá giới hạn.");
    }
    private async Task NormalizeDocuments(ExportPreparationContent content, Guid carrier, CancellationToken ct)
    {
        foreach (var doc in content.SharedDocuments.Concat(content.Containers.SelectMany(c => c.Documents)))
        {
            Require(doc != null && !string.IsNullOrWhiteSpace(doc.DocumentType) && doc.DocumentType.Length <= 100 && (doc.DocumentNumber?.Length??0)<=200 && (doc.Issuer?.Length??0)<=500 && doc.DocumentId.HasValue && doc.ContainerNumbers is { Count: <= 500 } && doc.ContainerNumbers.All(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 20), "Documents", "Chọn chứng từ được tải lên từ hồ sơ công ty.");
            var file = await Rows("""
                SELECT jsonb_build_object('key',object_key,'name',original_name,'size',size_bytes)::text AS "Value"
                FROM export_document_files WHERE id=@p0 AND carrier_id=@p1
                """, ct, doc!.DocumentId, carrier);
            Require(file.Count == 1, "Documents", "Chứng từ không thuộc công ty hoặc không còn khả dụng.");
            doc.FileUrl = $"https://{_documentBucket}.s3.amazonaws.com/{S(file[0], "key")}";
            doc.FileName = S(file[0], "name"); doc.FileSizeBytes = file[0]["size"]!.GetValue<long>(); doc.BookingOperationId = null;
        }
    }
    private async Task ResolvePreparedSources(ExportPreparationContent content, Guid carrier, Guid? bookingId, bool reserve, CancellationToken ct)
    {
        foreach (var c in content.Containers.OrderBy(c => c.ContainerId))
        {
            if (reserve) await Exec("SELECT id FROM containers WHERE id=@p0 FOR UPDATE", ct, c.ContainerId);
            var sources = await Rows("""
                SELECT jsonb_build_object('number',c.container_no,'type',t.code,'cargo',d.cargo_type::text,'weight',d.gross_weight_kg,'power',d.required_power_kw,'seal',d.seal_number)::text AS "Value"
                FROM container_declarations d JOIN containers c ON c.id=d.container_id JOIN container_types t ON t.id=c.container_type_id
                WHERE d.id=@p0 AND d.container_id=@p1 AND d.carrier_id=@p2 AND d.load_status='full'
                """, ct, c.DeclarationId, c.ContainerId, carrier);
            Require(sources.Count == 1, "Container", "Bản khai container không thuộc công ty hoặc không có hàng.");
            var source = sources[0]; c.ContainerNumber = S(source, "number"); c.ContainerTypeCode = S(source, "type"); c.CargoType = S(source, "cargo");
            c.GrossWeightKg = source["weight"]?.GetValue<decimal?>(); c.RequiredPowerKw = source["power"]?.GetValue<decimal?>();
            if (string.IsNullOrWhiteSpace(c.SealNumber)) c.SealNumber = S(source, "seal");
            Require(ValidIso6346(c.ContainerNumber), "Container", $"{c.ContainerNumber}: số container không đúng ISO 6346.");
            if (!reserve) continue;
            var blocked = await Rows("""
                SELECT jsonb_build_object('count',
                  (SELECT count(*) FROM export_source_reservations r WHERE r.container_id=@p0 AND r.active AND (@p1::uuid IS NULL OR r.export_booking_id<>@p1))+
                  (SELECT count(*) FROM export_booking_containers e WHERE e.container_id=@p0 AND e.active AND (@p1::uuid IS NULL OR e.export_booking_id<>@p1))+
                  (SELECT count(*) FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id WHERE bc.declaration_id=@p2 AND b.status::text NOT IN ('canceled','rejected','expired') AND NOT EXISTS(SELECT 1 FROM export_gate_links l WHERE l.gate_booking_id=b.id AND l.export_booking_id=@p1))+
                  (SELECT count(*) FROM container_visits v WHERE v.container_id=@p0 AND v.visit_status IN ('planned','active') AND NOT EXISTS(SELECT 1 FROM export_booking_containers e WHERE e.container_id=@p0 AND e.export_booking_id=@p1))
                )::text AS "Value"
                """, ct, c.ContainerId, bookingId, c.DeclarationId);
            Require(blocked[0]["count"]!.GetValue<long>() == 0, "Container", $"{c.ContainerNumber}: đã có hồ sơ/lượt bãi còn hiệu lực. Tải lại danh sách container.");
        }
        Require(content.Containers.Select(c => c.CargoType).Distinct().Count() <= 1, "CargoType", "Các container trong lô phải cùng loại hàng.");
    }
    private async Task<List<ExportEquipmentRequest>> Equipment(ExportPreparationContent content, CancellationToken ct)
    {
        var demand = content.Mode == "containers" ? content.Containers.GroupBy(c => c.ContainerTypeCode).Select(g => new ExportEquipmentRequest { TypeCode = g.Key!, Quantity = g.Count() }).ToList() : content.EquipmentRequests;
        Require(demand.Count > 0 && demand.All(d => d.Quantity is >= 1 and <= 50 && !string.IsNullOrWhiteSpace(d.TypeCode)) && demand.Sum(d => d.Quantity) <= 50, "Equipment", "Chọn nhu cầu từ 1 đến 50 container.");
        foreach (var d in demand) d.TypeCode = d.TypeCode.Trim().ToUpperInvariant();
        Require(demand.Select(d => d.TypeCode).Distinct().Count() == demand.Count, "Equipment", "Mỗi loại container chỉ khai một dòng.");
        foreach (var d in demand) Require((await Rows("SELECT jsonb_build_object('code',code)::text AS \"Value\" FROM container_types WHERE code=@p0", ct, d.TypeCode)).Count == 1, "Equipment", "Loại container không có trong danh mục.");
        return demand;
    }
    private static Dictionary<string, int> ApprovedEquipment(JsonObject booking)
    {
        var demand = booking["preparation"]?["equipmentRequests"]?.Deserialize<List<ExportEquipmentRequest>>(Json);
        return demand is { Count: > 0 } ? demand.ToDictionary(d => d.TypeCode, d => d.Quantity, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase) { [S(booking, "containerTypeCode")] = I(booking, "quantity") };
    }
    private async Task StorePreparedReservations(Guid bookingId, ExportPreparationContent content, CancellationToken ct)
    {
        await Exec("UPDATE export_source_reservations SET active=false WHERE export_booking_id=@p0", ct, bookingId);
        foreach (var c in content.Containers)
            await Exec("""
                INSERT INTO export_source_reservations(export_booking_id,container_id,declaration_id) VALUES(@p0,@p1,@p2)
                ON CONFLICT(export_booking_id,container_id) DO UPDATE SET declaration_id=excluded.declaration_id,active=true
                """, ct, bookingId, c.ContainerId, c.DeclarationId);
    }
    private async Task<JsonObject> Draft(Guid id, Guid carrier, bool locked, CancellationToken ct)
    {
        var rows = await Rows("SELECT row_to_json(x)::text AS \"Value\" FROM (" + DraftSelect + " WHERE id=@p0 AND carrier_id=@p1" + (locked ? " FOR UPDATE" : "") + ") x", ct, id, carrier);
        if (rows.Count != 1) throw new NotFoundException("ExportDraft", id);
        return rows[0];
    }
    public async Task<JsonObject> SaveDraft(Guid id, ExportDraftSaveRequest request, CancellationToken ct)
    {
        var carrier = await Company(ct); Require(id != Guid.Empty && request.RequestId != Guid.Empty, "RequestId", "Thiếu mã bản nháp hoặc mã lưu chống trùng.");
        PreparationShape(request.Content); await ResolvePreparedSources(request.Content, carrier, null, false, ct); await NormalizeDocuments(request.Content, carrier, ct);
        if(request.Content.BookingId.HasValue) { var booking=await Booking(request.Content.BookingId.Value,false,ct); await Own(booking,ct); Require(booking["preparation"]!=null,"Preparation","Không thể ghép nháp với hồ sơ khác quy trình."); }
        var hash = Hash(request.Content);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))", ct, "export-draft:" + id);
        var present = await Rows("SELECT jsonb_build_object('company',carrier_id)::text AS \"Value\" FROM export_booking_drafts WHERE id=@p0", ct, id);
        if (present.Count > 0)
        {
            if (G(present[0], "company") != carrier) throw new NotFoundException("ExportDraft", id);
            var draft = await Draft(id, carrier, true, ct);
            Require(draft["submittedBookingId"] == null, "Draft", "Bản nháp đã được gửi. Mở hồ sơ đã gửi để tiếp tục.");
            if (S(draft, "lastRequestId") == request.RequestId.ToString())
                Require(S(draft, "hash") == hash, "RequestId", "Mã lưu đã được dùng với nội dung khác.");
            else
            {
                if (I(draft, "version") != request.ExpectedVersion) throw new ExportDraftConflictException();
                await Exec("UPDATE export_booking_drafts SET content=@p0::jsonb,version=version+1,last_request_id=@p1,content_hash=@p2,updated_by=@p3,updated_at=now() WHERE id=@p4", ct, JsonSerializer.Serialize(request.Content, Json), request.RequestId, hash, Actor, id);
            }
        }
        else
        {
            Require(request.ExpectedVersion == 0, "Version", "Bản nháp chưa tồn tại. Tải lại danh sách nháp.");
            await Exec("INSERT INTO export_booking_drafts(id,carrier_id,created_by,updated_by,content,last_request_id,content_hash) VALUES(@p0,@p1,@p2,@p2,@p3::jsonb,@p4,@p5)", ct, id, carrier, Actor, JsonSerializer.Serialize(request.Content, Json), request.RequestId, hash);
        }
        await tx.CommitAsync(ct); return await GetDraft(id, ct);
    }
    public async Task<JsonObject> GetDraft(Guid id, CancellationToken ct)
    { var carrier=await Company(ct); var row = await Draft(id,carrier,false,ct); row.Remove("hash"); row.Remove("lastRequestId"); await RenewDocumentLinks(row,carrier,ct); return row; }
    public async Task<JsonObject> Drafts(CancellationToken ct)
    {
        var carrier = await Company(ct);
        var rows = await Rows("""
            SELECT row_to_json(x)::text AS "Value" FROM (SELECT id,version,updated_at AS "updatedAt",content->>'mode' AS mode,
              content->>'description' AS description,jsonb_array_length(coalesce(content->'containers','[]'::jsonb)) AS "containerCount"
            FROM export_booking_drafts WHERE carrier_id=@p0 AND submitted_booking_id IS NULL ORDER BY updated_at DESC LIMIT 50) x
            """, ct, carrier);
        return new JsonObject { ["items"] = new JsonArray(rows.Select(r => (JsonNode)r).ToArray()) };
    }
    public async Task<JsonObject> SubmitPreparation(ExportPreparationRequest request, CancellationToken ct)
    {
        var carrier = await Company(ct); PreparationShape(request.Content); Require(request.RequestId != Guid.Empty, "RequestId", "Thiếu mã gửi chống trùng.");
        var content = request.Content;
        content.BookingId=request.BookingId;
        if (content.Mode == "capacity") content.Containers.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))", ct, "export-request:" + carrier + ":" + request.RequestId);
        // URL signatures are presentation data; normalized document identity determines retries.
        await NormalizeDocuments(content, carrier, ct);
        await ResolvePreparedSources(content, carrier, null, false, ct);
        content.EquipmentRequests = await Equipment(content, ct);
        var fingerprint = Hash(content);
        var replay = await Rows("""
            SELECT jsonb_build_object('id',id,'hash',coalesce(preparation->'requestReceipts'->>@p1::text,request_hash))::text AS "Value"
            FROM export_bookings WHERE carrier_id=@p0 AND (request_id=@p1 OR preparation->'requestReceipts' ? @p1::text)
            """, ct, carrier, request.RequestId);
        if (replay.Count > 0) { Require(S(replay[0], "hash") == fingerprint && (!request.BookingId.HasValue || request.BookingId==G(replay[0],"id")), "RequestId", "Mã gửi đã dùng với nội dung khác."); await tx.CommitAsync(ct); return await Detail(G(replay[0], "id"), ct); }
        var previous = request.BookingId.HasValue ? await Booking(request.BookingId.Value, true, ct) : null;
        if (previous != null)
        {
            await Own(previous, ct); Version(previous, request.ExpectedVersion);
            Require(S(previous, "status") is "Pending" or "Rejected" && previous["preparation"] != null, "Status", "Chỉ sửa yêu cầu theo mốc đang chờ duyệt hoặc bị trả lại.");
        }
        var draft = request.DraftId.HasValue ? await Draft(request.DraftId.Value, carrier, true, ct) : null;
        if (draft != null) { Require(draft["submittedBookingId"] == null, "Draft", "Bản nháp đã được gửi."); if (I(draft, "version") != request.ExpectedDraftVersion) throw new ExportDraftConflictException(); }
        Require(content.ShippingLineId.HasValue && content.VoyageId.HasValue, "Voyage", "Chọn hãng tàu và chuyến còn mở.");
        var voyage = await Voyage(content.VoyageId!.Value, true, ct);
        Require(G(voyage, "shippingLineId") == content.ShippingLineId && S(voyage, "status") == "Scheduled" && D(voyage, "vgmCutoff") > DateTime.UtcNow && D(voyage,"cyCutoff")>DateTime.UtcNow, "Voyage", "Chuyến không còn mở hoặc không thuộc hãng tàu đã chọn.");
        Require((await Rows("SELECT jsonb_build_object('id',id)::text AS \"Value\" FROM users WHERE id=@p0 AND role='Shipping Line' AND is_active", ct, content.ShippingLineId)).Count == 1, "ShippingLineId", "Hãng tàu không còn hoạt động.");
        await ResolvePreparedSources(content, carrier, request.BookingId, true, ct);
        var id = request.BookingId ?? Guid.NewGuid(); var types = content.EquipmentRequests;
        var snapshot=JsonSerializer.SerializeToNode(content,Json)!.AsObject();
        var receipts=previous?["preparation"]?["requestReceipts"]?.DeepClone()?.AsObject()??new JsonObject();
        receipts[request.RequestId.ToString()]=fingerprint; snapshot["requestReceipts"]=receipts;
        if (previous == null) await Exec("""
            INSERT INTO export_bookings(id,request_id,request_hash,carrier_id,line_user_id,voyage_id,booking_no,container_type_code,quantity,commodity_description,created_by,preparation)
            VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11::jsonb)
            """, ct, id, request.RequestId, fingerprint, carrier, content.ShippingLineId, content.VoyageId, "EXP-" + id.ToString("N").ToUpperInvariant(), types.Count == 1 ? types[0].TypeCode : "MIXED", types.Sum(t => t.Quantity), string.IsNullOrWhiteSpace(content.Description) ? "Yêu cầu đặt chỗ xuất khẩu" : content.Description.Trim(), Actor, snapshot.ToJsonString());
        else await Exec("""
            UPDATE export_bookings SET request_id=@p0,request_hash=@p1,line_user_id=@p2,voyage_id=@p3,container_type_code=@p4,quantity=@p5,
              commodity_description=@p6,preparation=@p7::jsonb,status='Pending',port_status='Pending',version=version+1,updated_at=now() WHERE id=@p8
            """, ct, request.RequestId, fingerprint, content.ShippingLineId, content.VoyageId, types.Count == 1 ? types[0].TypeCode : "MIXED", types.Sum(t => t.Quantity), string.IsNullOrWhiteSpace(content.Description) ? "Yêu cầu đặt chỗ xuất khẩu" : content.Description.Trim(), snapshot.ToJsonString(), id);
        await StorePreparedReservations(id, content, ct);
        if (draft != null) await Exec("UPDATE export_booking_drafts SET submitted_booking_id=@p0,updated_at=now() WHERE id=@p1", ct, id, request.DraftId);
        await History(id, content.VoyageId.Value, "BookingRequested", "Khách hàng gửi nhu cầu đặt chỗ; VGM và lịch xe được hoàn thiện theo từng mốc, chưa tạo lịch cảng.", ct);
        await tx.CommitAsync(ct); return await Detail(id, ct);
    }
    private async Task<List<string>> PreparedCargoErrors(ExportPreparationContent content, CancellationToken ct)
    {
        var errors = new List<string>();
        if (content.Containers.Count == 0) errors.Add("Chọn container có hàng để khai danh sách.");
        foreach (var c in content.Containers)
        {
            if (string.IsNullOrWhiteSpace(c.SealNumber)) errors.Add($"{c.ContainerNumber}: thiếu số seal.");
            if (!(c.VgmKg > 0)) errors.Add($"{c.ContainerNumber}: bổ sung VGM thực tế đã xác minh.");
            if (c.CargoType == "reefer" && !c.TargetTemperature.HasValue) errors.Add($"{c.ContainerNumber}: bổ sung nhiệt độ bảo quản.");
            if ((c.CargoType is "dangerous" or "dangerous_goods") && (string.IsNullOrWhiteSpace(c.UnNumber) || string.IsNullOrWhiteSpace(c.DgClass))) errors.Add($"{c.ContainerNumber}: bổ sung mã UN và phân lớp nguy hiểm.");
            var docs = content.SharedDocuments.Concat(c.Documents).ToList();
            foreach (var spec in _documents.GetRequiredDocuments("Export", "Loaded", PolicyCargo(c.CargoType)).Where(s => s.IsMandatory && s.DocumentType is not ("CarrierBookingReference" or "VgmCertificate")))
                if (!docs.Any(d => d.DocumentType == spec.DocumentType)) errors.Add($"{c.ContainerNumber}: thiếu {spec.NameVi}.");
            foreach (var doc in docs) CheckOriginalDocument(doc);
            foreach(var doc in c.Documents)
                if(!doc.ContainerNumbers.Contains(c.ContainerNumber!,StringComparer.OrdinalIgnoreCase))errors.Add($"{c.ContainerNumber}: phạm vi chứng từ riêng không bao gồm container này. Đối chiếu lại tệp gốc.");
        }
        if (content.Containers.Count > 1 && content.SharedDocuments.Count > 0)
        {
            if (!content.SharedDocumentsConfirmed) errors.Add("Xác nhận phạm vi chứng từ dùng chung.");
            var numbers = content.Containers.Select(c => c.ContainerNumber!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in content.SharedDocuments) if (!numbers.IsSubsetOf(doc.ContainerNumbers)) errors.Add("Chứng từ chung phải bao phủ đầy đủ container trong lô; đối chiếu tệp gốc trước khi xác nhận.");
        }
        await Task.CompletedTask; return errors;
    }
    public async Task<JsonObject> PreparationReadiness(ExportPreparationContent content, CancellationToken ct)
    {
        var carrier = await Company(ct); PreparationShape(content);
        await ResolvePreparedSources(content, carrier, null, false, ct); await NormalizeDocuments(content, carrier, ct);
        var errors = await PreparedCargoErrors(content, ct);
        return new JsonObject { ["cargoReady"] = errors.Count == 0, ["messages"] = JsonSerializer.SerializeToNode(errors, Json), ["containerCount"] = content.Containers.Count, ["documentOcrAvailable"] = false };
    }
    public async Task<JsonObject> ConfirmPreparation(Guid id, ExportPreparationRequest request, CancellationToken ct)
    {
        var carrier = await Company(ct); PreparationShape(request.Content);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var booking = await Booking(id, true, ct); await Own(booking, ct);
        Require(booking["preparation"] != null && booking["submission"] == null, "Preparation", "Hồ sơ không sử dụng quy trình bổ sung theo mốc.");
        Require(request.RequestId != Guid.Empty, "RequestId", "Thiếu mã xác nhận chống trùng.");
        var content = request.Content;
        content.BookingId=id;
        await NormalizeDocuments(content, carrier, ct); await ResolvePreparedSources(content, carrier, id, false, ct);
        content.EquipmentRequests = ApprovedEquipment(booking).Select(d => new ExportEquipmentRequest { TypeCode = d.Key, Quantity = d.Value }).ToList();
        content.ShippingLineId = G(booking, "shippingLineId"); content.VoyageId = G(booking, "voyageId"); content.Mode = "containers";
        var hash = Hash(content);
        if (S(booking["preparation"], "confirmationRequestId") == request.RequestId.ToString())
        { Require(S(booking["preparation"], "confirmationHash") == hash, "RequestId", "Mã xác nhận đã được dùng với nội dung khác."); await tx.CommitAsync(ct); return await Detail(id, ct); }
        Version(booking, request.ExpectedVersion);
        var draft=request.DraftId.HasValue?await Draft(request.DraftId.Value,carrier,true,ct):null;
        if(draft!=null) { Require(draft["submittedBookingId"]==null,"Draft","Bản nháp đã được gửi."); if(I(draft,"version")!=request.ExpectedDraftVersion)throw new ExportDraftConflictException(); }
        Require(S(booking,"status") is "Approved" or "ContainersDeclared", "Status", "Hãng tàu cần duyệt đặt chỗ trước khi nhận danh sách container.");
        Require(S(booking,"siStatus")!="Confirmed","SI","SI đã xác nhận; cần hãng tàu yêu cầu sửa trước khi đổi danh sách container.");
        Require((await Containers(id,ct)).All(c => c["hasGateHistory"]?.GetValue<bool>() != true), "Containers", "Danh sách đã có lịch vận tải; liên hệ hãng tàu xử lý điều chỉnh.");
        var voyage = await Voyage(G(booking,"voyageId"),true,ct);
        Require(S(voyage,"status")=="Scheduled" && D(voyage,"vgmCutoff")>DateTime.UtcNow && D(voyage,"cyCutoff")>DateTime.UtcNow,"Cutoff","Chuyến đã đóng hoặc quá hạn VGM/CY.");
        await ResolvePreparedSources(content,carrier,id,true,ct);
        var errors = await PreparedCargoErrors(content, ct);
        Require(errors.Count == 0, "Documents", string.Join("\n", errors));
        var demand = ApprovedEquipment(booking);
        Require(content.Containers.Count==I(booking,"quantity") && content.Containers.GroupBy(c => c.ContainerTypeCode).All(g => demand.TryGetValue(g.Key!, out var quantity) && g.Count() == quantity), "Equipment", "Khai đủ số lượng theo từng loại container đã được duyệt trước khi gửi danh sách.");
        var stored = JsonSerializer.SerializeToNode(content, Json)!.AsObject(); stored["confirmationRequestId"] = request.RequestId.ToString(); stored["confirmationHash"] = hash;
        stored["requestReceipts"]=booking["preparation"]?["requestReceipts"]?.DeepClone();
        stored["containerReview"] = "Pending";
        await Exec("UPDATE export_bookings SET preparation=@p0::jsonb,port_status='WaitingContainerReview',version=version+1,updated_at=now() WHERE id=@p1", ct, stored.ToJsonString(), id);
        await StorePreparedReservations(id, content, ct);
        if(draft!=null)await Exec("UPDATE export_booking_drafts SET submitted_booking_id=@p0,updated_at=now() WHERE id=@p1",ct,id,request.DraftId);
        await History(id,G(booking,"voyageId"),"ContainerListSubmitted","Khách hàng gửi phiên bản container/VGM/chứng từ để hãng tàu kiểm tra trước khi chuyển cảng.",ct);
        await tx.CommitAsync(ct); return await Detail(id, ct);
    }
    public async Task<JsonObject> ReviewPreparedContainers(Guid id, ExportContainerReviewRequest request, CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var booking=await Booking(id,true,ct); OwnLine(booking); Version(booking,request.ExpectedVersion);
        Require(booking["preparation"] != null && S(booking,"portStatus")=="WaitingContainerReview","Status","Chưa có phiên bản container đang chờ hãng tàu kiểm tra.");
        var content=booking["preparation"]!.Deserialize<ExportPreparationContent>(Json)!;
        if(request.Approve)
        {
            await Voyage(G(booking,"voyageId"),true,ct);
            await ResolvePreparedSources(content,G(booking,"carrierId"),id,true,ct);
            await NormalizeDocuments(content,G(booking,"carrierId"),ct);
            var errors=await PreparedCargoErrors(content,ct); Require(errors.Count==0,"Documents",string.Join("\n",errors));
            await Declare(id,new DeclareExportContainersRequest { ExpectedVersion=request.ExpectedVersion,Containers=content.Containers.Select(c=>new ExportContainerInput {
                ContainerId=c.ContainerId,DeclarationId=c.DeclarationId,SealNumber=c.SealNumber,VgmKg=c.VgmKg!.Value,
                TargetTemperature=c.TargetTemperature,UnNumber=c.UnNumber,DgClass=c.DgClass,
                CustomsDocumentUrl=content.SharedDocuments.Concat(c.Documents).FirstOrDefault(d=>d.DocumentType=="CustomsDeclaration")?.FileUrl
            }).ToList() },ct,true);
        }
        else
        {
            Text(request.Reason??"","Lý do",1000);
            await Exec("UPDATE export_bookings SET port_status='ContainerRevisionRequested',version=version+1,updated_at=now() WHERE id=@p0",ct,id);
            await History(id,G(booking,"voyageId"),"ContainerRevisionRequested",request.Reason!,ct);
        }
        var stored=booking["preparation"]!.AsObject(); stored["containerReview"]=request.Approve?"Approved":"RevisionRequested";
        await Exec("UPDATE export_bookings SET preparation=@p0::jsonb WHERE id=@p1",ct,stored.ToJsonString(),id);
        await tx.CommitAsync(ct); return await Detail(id,ct);
    }
    public async Task<JsonObject> UploadPreparationDocument(IFormFile file, CancellationToken ct)
    {
        var carrier = await Company(ct);
        Require(file != null && file.Length is > 0 and <= 26214400, "File", "Chọn tệp chứng từ từ 1 byte đến 25 MB.");
        var ext = Path.GetExtension(file!.FileName).ToLowerInvariant();
        Require(new[] { ".pdf", ".jpg", ".jpeg", ".png", ".webp" }.Contains(ext), "File", "Chỉ hỗ trợ PDF, JPG, PNG và WEBP.");
        Require(_storage != null, "Storage", "Chưa có dịch vụ AWS S3 cho chứng từ.");
        using var memory = new MemoryStream(); await file.CopyToAsync(memory, ct);
        var hash = Convert.ToHexString(SHA256.HashData(memory.ToArray())); memory.Position = 0;
        string url;
        try { url=await _storage!.UploadFileAsync(memory,file.FileName,file.ContentType,"documents",ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch { throw new ExportDocumentStorageException(); }
        var key = DocumentObjectKey(url); if(key==null)throw new ExportDocumentStorageException();
        var id = Guid.NewGuid();
        await Exec("INSERT INTO export_document_files(id,carrier_id,uploaded_by,object_key,original_name,content_type,size_bytes,sha256) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)", ct, id, carrier, Actor, key, Path.GetFileName(file.FileName)[..Math.Min(500, Path.GetFileName(file.FileName).Length)], file.ContentType[..Math.Min(150, file.ContentType.Length)], file.Length, hash);
        return new JsonObject { ["documentId"] = id.ToString(), ["fileUrl"] = url, ["fileName"] = Path.GetFileName(file.FileName), ["fileSizeBytes"] = file.Length, ["sha256"] = hash, ["extractionStatus"] = "NotRequested" };
    }
    private string? DocumentObjectKey(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        var host = uri.Host; var key = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (host.StartsWith(_documentBucket + ".s3.", StringComparison.OrdinalIgnoreCase) && host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)) { }
        else if (host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase) && host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) && key.StartsWith(_documentBucket + "/", StringComparison.Ordinal)) key = key[(_documentBucket.Length + 1)..];
        else return null;
        return key.StartsWith("NexusPort/documents/", StringComparison.Ordinal) && !key.Split('/').Any(p => p is ".." or ".") ? key : null;
    }
    private async Task RenewDocumentLinks(JsonNode? node,Guid carrier,CancellationToken ct)
    {
        if (_storage == null || node == null) return;
        var candidates=new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode? item)
        {
            if(item is JsonObject obj)foreach(var property in obj)
            {
                if((property.Key is "fileUrl" or "customsDocumentUrl") && property.Value is JsonValue value && value.TryGetValue<string>(out var url) && DocumentObjectKey(url) is { } key)candidates.Add(key);
                else Collect(property.Value);
            }
            else if(item is JsonArray array)foreach(var child in array)Collect(child);
        }
        Collect(node); if(candidates.Count==0)return;
        var files=await Rows("SELECT jsonb_build_object('key',object_key)::text AS \"Value\" FROM export_document_files WHERE carrier_id=@p0 AND object_key=ANY(@p1)",ct,carrier,candidates.ToArray());
        var allowed=files.Select(file=>S(file,"key")).ToHashSet(StringComparer.Ordinal);
        SignDocumentLinks(node,allowed);
    }
    private void SignDocumentLinks(JsonNode? node,HashSet<string> allowed)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
                if ((property.Key is "fileUrl" or "customsDocumentUrl") && property.Value is JsonValue value && value.TryGetValue<string>(out var url) && DocumentObjectKey(url) is { } key && allowed.Contains(key)) obj[property.Key] = _storage!.GetPresignedUrl(key, 1);
                else SignDocumentLinks(property.Value,allowed);
        }
        else if (node is JsonArray array) foreach (var child in array) SignDocumentLinks(child,allowed);
    }
    public async Task<JsonObject> SiSuggestions(Guid id, CancellationToken ct)
    {
        var booking = await Booking(id, false, ct); await Own(booking, ct);
        var carrier = G(booking, "carrierId");
        var templates = await Rows("""
            SELECT jsonb_build_object('bookingNumber',booking_no,'shipper',si->>'shipper','consignee',si->>'consignee',
            'notifyParty',si->>'notifyParty','freightTerms',si->>'freightTerms','blType',si->>'blType')::text AS "Value"
            FROM export_bookings WHERE carrier_id=@p0 AND id<>@p1 AND si_status='Confirmed' AND si IS NOT NULL ORDER BY created_at DESC LIMIT 5
            """, ct, carrier, id);
        var weight = await Rows("""
            SELECT jsonb_build_object('count',count(*)::int,'known',count(d.gross_weight_kg) FILTER(WHERE d.gross_weight_kg>0)::int,'weight',sum(d.gross_weight_kg),
              'total',(SELECT count(*)::int FROM export_booking_containers all_sources WHERE all_sources.export_booking_id=@p0))::text AS "Value"
            FROM export_booking_containers e JOIN container_declarations d ON d.id=e.declaration_id WHERE e.export_booking_id=@p0 AND e.status NOT IN ('Excluded','Rolled')
            """, ct, id);
        var w = weight[0];
        return new JsonObject { ["templates"] = new JsonArray(templates.Select(t => (JsonNode)t).ToArray()), ["grossWeightKg"] = I(w,"total")==I(booking,"quantity") && I(w,"count")>0 && I(w, "known") == I(w, "count") ? w["weight"]?.DeepClone() : null, ["sourceCount"] = I(w, "count") };
    }
}

sealed class ExportDraftConflictException : AppException
{
    public ExportDraftConflictException() : base("Bản nháp đã được cập nhật ở phiên khác. Tải lại bản nháp trước khi tiếp tục; thay đổi đang nhập được giữ trên màn hình.", 409, "EXPORT_DRAFT_CONFLICT") { }
}

sealed class ExportDocumentStorageException : AppException
{
    public ExportDocumentStorageException() : base("Chưa tải được chứng từ lên AWS S3. Hồ sơ đang nhập được giữ; vui lòng thử lại.",503,"EXPORT_DOCUMENT_STORAGE_UNAVAILABLE") { }
}

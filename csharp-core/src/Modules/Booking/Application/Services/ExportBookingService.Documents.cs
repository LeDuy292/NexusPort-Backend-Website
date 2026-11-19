using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

/// <summary>
/// Chứng từ chiều xuất: hạn xác nhận B/L draft, yêu cầu sửa của shipper, phí sửa đổi, pre-alert và lịch sử hồ sơ.
/// </summary>
public sealed partial class ExportBookingService
{
    private static readonly string[] AmendableSiFields =
        { "shipper", "consignee", "notifyParty", "cargoDescription", "packagesCount", "grossWeightKg", "freightTerms", "blType", "destinationPort" };
    private static readonly string[] BlTypes = { "Original", "TelexRelease", "SeawayBill" };
    private static readonly string[] ClosedStatuses = { "Pending", "Rejected", "Cancelled", "BlIssued" };

    /// <summary>Hạn xác nhận draft: thường trước ETD 12 giờ, tối thiểu 2 giờ và tối đa 48 giờ kể từ lúc gửi draft.</summary>
    private static DateTime DraftDeadline(JsonObject voyage, DateTime now)
    {
        var min = now.AddHours(2);
        var max = now.AddHours(48);
        var beforeEtd = D(voyage, "etd").AddHours(-12);
        var deadline = beforeEtd < max ? beforeEtd : max;
        return deadline < min ? min : deadline;
    }

    private decimal AmendmentFeeVnd => decimal.TryParse(_configuration?["Export:AmendmentFeeVnd"], out var fee) && fee >= 0 ? fee : 500000m;

    /// <summary>
    /// Ghi lịch sử chứng từ vào shipment_document_events. Gọi SAU khi commit: lỗi ghi lịch sử không được làm hỏng nghiệp vụ.
    /// </summary>
    private async Task DocEvent(string fileRef, string documentType, string action, string? fromStatus, string? toStatus,
        string? note, int? version, JsonNode? fieldChanges, JsonNode? snapshot, CancellationToken ct, string? channel = null)
    {
        try
        {
            await Exec("""
                INSERT INTO shipment_document_events(direction,file_ref,document_type,action,from_status,to_status,actor_user_id,actor_username,actor_role,channel,field_changes,document_version,snapshot,note)
                VALUES('EXPORT',@p0,@p1,@p2,@p3,@p4,@p5::uuid,@p6,@p7,@p8,COALESCE(@p9::jsonb,'[]'::jsonb),@p10::int,@p11::jsonb,@p12)
                """, ct, fileRef, documentType, action, fromStatus, toStatus, _user.UserId, _user.Username, _user.Role,
                channel ?? (_user.UserId == null ? "SYSTEM" : "PORTAL"), fieldChanges?.ToJsonString(), version, snapshot?.ToJsonString(), note);
        }
        catch { /* lịch sử bổ sung; export_history vẫn là bản ghi chính */ }
    }

    /// <summary>Thông báo cho công ty chủ hàng (bảng cargo_notifications). Gọi SAU khi commit, lỗi được bỏ qua.</summary>
    private async Task NotifyCompany(Guid carrierId, string bookingNo, string eventType, string priority, string title, string message,
        string dedupKey, CancellationToken ct)
    {
        try
        {
            await Exec("""
                INSERT INTO cargo_notifications(recipient_company_id,company_tax_code,recipient_role,event_type,direction,priority,title,message,data,action_type,action_label,action_url,document_type,document_id,dedup_key)
                SELECT c.id,c.tax_code,'Transport Company',@p1,'export',@p2,@p3,@p4,jsonb_build_object('bookingNo',@p5::text),'view_details','Xem chi tiết',@p6,'BOOKING',@p5::text,@p7
                FROM carriers c WHERE c.id=@p0 AND NOT EXISTS (SELECT 1 FROM cargo_notifications n WHERE n.dedup_key=@p7) LIMIT 1
                """, ct, carrierId, eventType, priority, title, message, bookingNo, "/booking?bookingNo=" + Uri.EscapeDataString(bookingNo), dedupKey);
        }
        catch { }
    }

    private static JsonObject SiSnapshot(JsonObject b) => b["si"] is JsonObject si ? (JsonObject)si.DeepClone() : new JsonObject();

    private static JsonNode? SiValue(JsonObject si, string field) => si[field]?.DeepClone();

    /// <summary>Kiểm tra giá trị mới của một trường SI theo cùng quy tắc với lúc nộp SI.</summary>
    private static JsonNode ParseAmendedValue(string field, string? raw, JsonObject voyage)
    {
        Require(AmendableSiFields.Contains(field), "Field", $"Trường '{field}' không được phép sửa qua yêu cầu sửa draft.");
        var value = (raw ?? "").Trim();
        Require(value.Length > 0, field, $"Giá trị mới của '{field}' không được để trống.");
        switch (field)
        {
            case "shipper": case "consignee":
                Require(value.Length <= 1000, field, "Tối đa 1000 ký tự."); return value;
            case "notifyParty":
                Require(value.Length <= 1000, field, "Tối đa 1000 ký tự."); return value;
            case "cargoDescription":
                Require(value.Length <= 2000, field, "Tối đa 2000 ký tự."); return value;
            case "packagesCount":
                Require(int.TryParse(value, out var packages) && packages > 0, field, "Số kiện phải là số nguyên lớn hơn 0."); return packages;
            case "grossWeightKg":
                Require(decimal.TryParse(value, out var weight) && weight > 0, field, "Khối lượng phải lớn hơn 0."); return weight;
            case "freightTerms":
                Require(value is "Prepaid" or "Collect", field, "Điều kiện cước phải là Prepaid hoặc Collect."); return value;
            case "blType":
                Require(BlTypes.Contains(value), field, "Hình thức B/L phải là Original, TelexRelease hoặc SeawayBill."); return value;
            case "destinationPort":
                Require(string.Equals(value, S(voyage, "portOfDischarge"), StringComparison.OrdinalIgnoreCase), field, "Cảng đến phải khớp với Booking.");
                return S(voyage, "portOfDischarge");
            default: throw new ValidationException("Field", "Trường không hợp lệ.");
        }
    }

    /// <summary>Shipper gửi yêu cầu sửa B/L draft: ghi rõ trường cần sửa và nội dung mới. Sau hạn chót sẽ đề xuất phí sửa đổi.</summary>
    public async Task<JsonObject> RequestAmendment(Guid id, ExportAmendmentRequest request, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var b = await Booking(id, true, ct); await Own(b, ct); Version(b, request.ExpectedVersion);
        Require(!ClosedStatuses.Contains(S(b, "status")), "Status", "Booking không còn cho phép yêu cầu sửa draft.");
        Require(S(b, "siStatus") == "Draft" && b["draft"] != null && I(b, "draftVersion") == request.DraftVersion, "Draft",
            "Chỉ gửi yêu cầu sửa khi hãng tàu đã gửi draft và đúng phiên bản hiện hành. Tải lại để kiểm tra.");
        Text(request.Reason ?? "", "Lý do", 1000);
        Require(request.Changes.Count is > 0 and <= 10, "Changes", "Cần ghi rõ từ 1 đến 10 trường cần sửa.");
        Require(request.Changes.Select(x => x.Field).Distinct().Count() == request.Changes.Count, "Changes", "Mỗi trường chỉ được nêu một lần.");
        var v = await Voyage(G(b, "voyageId"), false, ct);
        var si = SiSnapshot(b);
        var changes = new JsonArray();
        foreach (var change in request.Changes)
        {
            var newValue = ParseAmendedValue(change.Field, change.NewValue, v);
            changes.Add(new JsonObject { ["field"] = change.Field, ["oldValue"] = SiValue(si, change.Field), ["newValue"] = newValue });
        }
        var deadline = b["draftDeadline"] == null ? (DateTime?)null : D(b, "draftDeadline");
        var late = deadline.HasValue && DateTime.UtcNow > deadline.Value;
        var fee = late ? AmendmentFeeVnd : (decimal?)null;
        var amendment = new JsonObject
        {
            ["requestedAt"] = DateTime.UtcNow, ["requestedBy"] = Actor, ["reason"] = request.Reason!.Trim(),
            ["draftVersion"] = request.DraftVersion, ["late"] = late, ["changes"] = changes
        };
        await Exec("UPDATE export_bookings SET si_status='AmendmentRequested',amendment_request=@p0::jsonb,amendment_fee_vnd=@p1,amendment_fee_status=@p2,version=version+1,updated_at=now() WHERE id=@p3",
            ct, amendment.ToJsonString(), fee, late ? "Proposed" : null, id);
        await History(id, G(b, "voyageId"), "DraftAmendmentRequested",
            "Khách hàng yêu cầu sửa B/L draft" + (late ? " sau hạn chốt (đề xuất phí sửa đổi)." : "."), ct);
        await tx.CommitAsync(ct);
        await DocEvent(S(b, "bookingNumber"), "DRAFT_BL", "REVISION_REQUESTED", "Draft", "AmendmentRequested", request.Reason, request.DraftVersion,
            JsonNode.Parse(changes.ToJsonString()), null, ct);
        return await Detail(id, ct);
    }

    /// <summary>Hãng tàu xử lý yêu cầu sửa: chấp nhận (cập nhật SI và lập draft mới) hoặc từ chối (giữ nguyên draft). Duyệt hoặc miễn phí sửa đổi.</summary>
    public async Task<JsonObject> ResolveAmendment(Guid id, ExportResolveAmendmentRequest request, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var b = await Booking(id, true, ct); await OwnLine(b,ct); Version(b, request.ExpectedVersion);
        Require(!ClosedStatuses.Contains(S(b, "status")), "Status", "Booking không còn cho phép xử lý yêu cầu sửa.");
        Require(S(b, "siStatus") == "AmendmentRequested" && b["amendmentRequest"] is JsonObject, "Amendment", "Không có yêu cầu sửa nào đang chờ xử lý.");
        var amendment = (JsonObject)b["amendmentRequest"]!;
        var feeProposed = S(b, "amendmentFeeStatus") == "Proposed";
        string historyText, action, toStatus;
        string? feeStatus = feeProposed ? (request.WaiveFee ? "Waived" : "Approved") : null;
        var bookingNo = S(b, "bookingNumber");
        JsonObject? newDraft = null;
        if (request.Accept)
        {
            var v = await Voyage(G(b, "voyageId"), false, ct);
            var si = SiSnapshot(b);
            var changes = amendment["changes"]!.AsArray().OfType<JsonObject>().ToList();
            var termsChanged = false;
            foreach (var change in changes)
            {
                var field = S(change, "field");
                if (field == "freightTerms" && S(si, "freightTerms") != change["newValue"]!.GetValue<string>()) termsChanged = true;
                si[field] = change["newValue"]!.DeepClone();
            }
            var containers = (await Containers(id, ct)).Where(x => !new[] { "Excluded", "Rolled" }.Contains(S(x, "status"))).ToList();
            Require(containers.Count > 0, "Containers", "Cần danh sách container để lập lại draft B/L.");
            Require(si["grossWeightKg"]!.GetValue<decimal>() <= containers.Sum(x => x["vgmKg"]!.GetValue<decimal>()), "GrossWeightKg",
                "Khối lượng hàng sau khi sửa không thể lớn hơn tổng VGM của danh sách container.");
            var nextVersion = I(b, "draftVersion") + 1;
            newDraft = new JsonObject { ["version"] = nextVersion, ["si"] = si, ["containers"] = new JsonArray(containers.Select(x => (JsonNode)x).ToArray()), ["preparedAt"] = DateTime.UtcNow, ["amendedFrom"] = request.Reason ?? "" };
            var deadline = DraftDeadline(v, DateTime.UtcNow);
            await Exec("UPDATE export_bookings SET si=@p0::jsonb,draft=@p1::jsonb,draft_version=@p2,draft_deadline=@p3,si_status='Draft',amendment_request=NULL,amendment_fee_status=@p4,freight_paid=CASE WHEN @p5 THEN false ELSE freight_paid END,version=version+1,updated_at=now() WHERE id=@p6",
                ct, si.ToJsonString(), newDraft.ToJsonString(), nextVersion, deadline, feeStatus, termsChanged, id);
            historyText = "Hãng tàu chấp nhận yêu cầu sửa và gửi lại B/L draft phiên bản " + nextVersion + ".";
            action = "AMENDMENT_ACCEPTED"; toStatus = "Draft";
        }
        else
        {
            Text(request.Reason ?? "", "Lý do từ chối", 1000);
            // Từ chối: draft cũ giữ nguyên và không phát sinh phí sửa đổi
            await Exec("UPDATE export_bookings SET si_status='Draft',amendment_request=NULL,amendment_fee_vnd=NULL,amendment_fee_status=NULL,version=version+1,updated_at=now() WHERE id=@p0", ct, id);
            historyText = "Hãng tàu từ chối yêu cầu sửa: " + request.Reason!.Trim();
            action = "AMENDMENT_REJECTED"; toStatus = "Draft"; feeStatus = null;
        }
        await History(id, G(b, "voyageId"), request.Accept ? "DraftAmendmentAccepted" : "DraftAmendmentRejected", historyText, ct);
        await tx.CommitAsync(ct);
        await DocEvent(bookingNo, "DRAFT_BL", action, "AmendmentRequested", toStatus, request.Reason, newDraft == null ? I(b, "draftVersion") : I(b, "draftVersion") + 1,
            amendment["changes"]!.DeepClone(), newDraft, ct);
        if (feeStatus != null)
            await DocEvent(bookingNo, "PAYMENT", feeStatus == "Waived" ? "AMENDMENT_FEE_WAIVED" : "AMENDMENT_FEE_APPROVED", null, feeStatus,
                $"Phí sửa đổi {b["amendmentFeeVnd"]} VND", null, null, null, ct);
        if (request.Accept)
            await NotifyCompany(G(b, "carrierId"), bookingNo, "DraftBLIssued", "high", $"B/L draft [{bookingNo}] đã được cập nhật theo yêu cầu sửa",
                "Hãng tàu đã chấp nhận yêu cầu sửa và gửi lại B/L draft. Vui lòng kiểm tra và xác nhận trước hạn chót.", $"draft-{bookingNo}-v{I(b, "draftVersion") + 1}", ct);
        return await Detail(id, ct);
    }

    /// <summary>Gửi thông báo khi hãng tàu lập draft (hạn chót và điều khoản phí sửa đổi).</summary>
    private Task NotifyDraftSent(JsonObject b, DateTime deadline, int draftVersion, CancellationToken ct)
        => NotifyCompany(G(b, "carrierId"), S(b, "bookingNumber"), "DraftBLIssued", "high",
            $"B/L draft [{S(b, "bookingNumber")}] cần xác nhận",
            $"Hãng tàu đã gửi B/L draft. Hạn xác nhận: {deadline.AddHours(7):dd/MM/yyyy HH:mm}. Yêu cầu sửa sau hạn chót sẽ bị tính phí sửa đổi; nếu quá hạn mà không phản hồi, draft được coi là đã chấp nhận.",
            $"draft-{S(b, "bookingNumber")}-v{draftVersion}", ct);

    /// <summary>Pre-alert gửi đầu nhập sau khi phát hành B/L, ghi cùng transaction với việc phát hành.</summary>
    private async Task CreatePreAlert(JsonObject b, JsonObject v, JsonObject issued, List<JsonObject> retained, string number, CancellationToken ct)
    {
        var si = issued["si"] as JsonObject ?? new JsonObject();
        var containers = new JsonArray(retained.Select(x => (JsonNode)new JsonObject
        {
            ["containerNumber"] = S(x, "containerNumber") is { Length: > 0 } cn ? cn : S(x, "container_no"),
            ["sealNumber"] = S(x, "sealNumber"), ["vgmKg"] = x["vgmKg"]?.DeepClone(), ["isoType"] = S(x, "typeCode")
        }).ToArray());
        var payload = new JsonObject
        {
            ["blNumber"] = number, ["bookingNo"] = S(b, "bookingNumber"), ["vesselName"] = S(v, "vesselName"), ["voyageNumber"] = S(v, "voyageNumber"),
            ["portOfLoading"] = S(v, "portOfLoading"), ["portOfDischarge"] = S(v, "portOfDischarge"), ["etd"] = v["etd"]?.DeepClone(), ["eta"] = v["eta"]?.DeepClone(),
            ["blType"] = S(si, "blType"), ["freightTerms"] = S(si, "freightTerms"), ["shipper"] = S(si, "shipper"), ["consignee"] = S(si, "consignee"),
            ["notifyParty"] = S(si, "notifyParty"), ["containers"] = containers, ["onBoardAt"] = issued["onBoardAt"]?.DeepClone(), ["issuedAt"] = issued["issuedAt"]?.DeepClone()
        };
        await Exec("""
            INSERT INTO shipment_pre_alerts(export_booking_id,booking_no,bl_number,vessel_name,voyage_number,port_of_loading,port_of_discharge,etd,eta,bl_type,freight_terms,shipper,consignee,notify_party,containers,payload)
            VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7::timestamptz,@p8::timestamptz,@p9,@p10,@p11,@p12,@p13,@p14::jsonb,@p15::jsonb)
            ON CONFLICT (bl_number) DO NOTHING
            """, ct, G(b, "id"), S(b, "bookingNumber"), number, S(v, "vesselName"), S(v, "voyageNumber"), S(v, "portOfLoading"), S(v, "portOfDischarge"),
            S(v, "etd"), S(v, "eta"), S(si, "blType"), S(si, "freightTerms"), S(si, "shipper"), S(si, "consignee"), S(si, "notifyParty"),
            containers.ToJsonString(), payload.ToJsonString());
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceDraftService
{
    public async Task<object> DeliveryProfile(CancellationToken ct)
    {
        var company = await Company(ct);
        var row = (await Rows("""
            SELECT COALESCE(to_jsonb(u)->>'full_name',to_jsonb(u)->>'username','') AS "contactName",
                COALESCE(to_jsonb(u)->>'phone','') AS "contactPhone",COALESCE(to_jsonb(u)->>'email','') AS email,
                c.company_name AS "companyName",COALESCE(to_jsonb(c)->>'tax_code','') AS "taxCode",
                COALESCE(to_jsonb(c)->>'address','') AS address
            FROM users u CROSS JOIN carriers c WHERE u.id=@p0 AND c.id=@p1
            """, ct, Actor, company)).Single();
        return row;
    }
    private async Task<ServiceDraftInput> DeliveryDefaults(ServiceDraftInput input, CancellationToken ct)
    {
        Require(input.WorkflowVersion is 0 or 2, "Phiên bản quy trình không hợp lệ.");
        if (input.WorkflowVersion != 2) return input;
        var until = input.Delivery.OrderValidUntil ?? input.Source.ValidUntil;
        var end = until == null ? null : DateTimeOffset.Parse(until + "T23:59:59+07:00", CultureInfo.InvariantCulture).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var timed = (await Rows("SELECT id FROM eport_services WHERE code IN ('STORAGE_EXTENSION','REEFER_POWER') AND unit IN ('DAY','HOUR')", ct)).Select(row => G(row, "id")).ToHashSet();
        return input with { Lines = input.Lines.Select(line => line with { Services = line.Services.Select(usage =>
            timed.Contains(usage.ServiceId) ? usage with { StartsAt = usage.StartsAt ?? input.PlannedAt, EndsAt = usage.EndsAt ?? end } : usage).ToArray() }).ToArray() };
    }
    public async Task<object> RegistrationEmailStatus(Guid id, ServiceDraftInput request, CancellationToken ct)
    {
        var company = await Company(ct); await GuardEmailDraft(id, company, ct);
        var input = ServiceDraftRules.Normalize(request);
        if (await HasSourceEmail(id, input, company, ct)) return new { verified = true, challenge = (JsonObject?)null };
        var email = await RegisteredEmail(ct);
        var pending = await Rows("""
            SELECT id AS "challengeId",GREATEST(0,ceil(extract(epoch FROM (created_at+interval '60 seconds'-now()))))::int AS "retryAfterSeconds",
                GREATEST(0,floor(extract(epoch FROM (expires_at-now()))))::int AS "expiresInSeconds"
            FROM eport_service_email_verifications WHERE draft_id=@p0 AND actor_id=@p1 AND carrier_id=@p2
                AND source_hash=@p3 AND email=@p4 AND invalidated_at IS NULL AND verified_at IS NULL AND attempts<5 AND expires_at>now()
            ORDER BY created_at DESC LIMIT 1
            """, ct, id, Actor, company, SourceHash(input), email);
        var challenge = pending.FirstOrDefault();
        if (challenge != null) challenge["maskedEmail"] = email[..1] + "***" + email[email.IndexOf('@')..];
        return new { verified = false, challenge };
    }
    public async Task<object> CheckRegistrationCustoms(Guid id, ServiceDraftInput request, CancellationToken ct)
    {
        var company = await Company(ct); await GuardEmailDraft(id, company, ct);
        var input = ServiceDraftRules.Normalize(request);
        // Scope selected occurrences before any future external call. No client
        // "cleared" value is accepted and no customs source row is modified.
        var snapshots = await Snapshots(company, input, ct);
        Require(input.Lines.Length > 0, "Chọn ít nhất một container trước khi đối chiếu thông quan.");
        var operational = (await OperationalRows(company, input, ct)).ToDictionary(row => G(row, "sourceContainerId"));
        var items = snapshots.Containers.Select(container =>
        {
            var sourceId = G(container!, "sourceContainerId");
            var cleared = operational.TryGetValue(sourceId, out var state) && state["customsCleared"]?.GetValue<bool>() == true;
            var demoCleared = state?["customsDemoCleared"]?.GetValue<bool>() == true;
            return new { sourceContainerId = sourceId, containerNumber = S(container, "containerNumber"),
                status = cleared ? "CLEARED_PORT_RECORD" : demoCleared ? "DEMO_CLEARED" : "NOT_VERIFIED", cleared, demoCleared };
        }).ToArray();
        var demo = items.Any(item => item.demoCleared);
        return new { flow = input.Flow, provider = demo ? "NEXUSPORT_DEMO" : "NEXUSPORT_PORT_RECORD", vasscmConnected = false,
            allCleared = items.All(item => item.cleared), allReadyForRegistration = items.All(item => item.cleared || item.demoCleared), demo,
            checkedAt = DateTimeOffset.UtcNow, items,
            notice = demo ? "DEMO: đã mô phỏng thông quan cho dữ liệu mẫu trong Development để thử luồng đăng ký. Không phải kết quả VASSCM và không cấp quyền qua cổng thực tế."
                : "Đối chiếu dữ liệu hiện có của cảng; chưa kết nối VASSCM. Nếu chưa có kết quả, có thể xác nhận tiếp tục đăng ký chờ cảng kiểm tra. Thanh toán không thay thế quyền giao nhận hoặc thông quan." };
    }
    private async Task<JsonArray> DeliveryIssues(Guid draftId, ServiceDraftInput input, Guid company,
        Dictionary<Guid, JsonObject> containers, Dictionary<Guid, JsonObject> services, Dictionary<Guid, JsonObject> operational, CancellationToken ct)
    {
        var issues = new JsonArray();
        void Add(string code, string message, string field, Guid? line = null, bool warning = false) => issues.Add(new JsonObject
            { ["code"] = code, ["message"] = message, ["field"] = field, ["sourceContainerId"] = line?.ToString(), ["severity"] = warning ? "WARNING" : "ERROR" });
        if (input.WorkflowVersion != 2) { Add("DELIVERY_WORKFLOW_REQUIRED", "Mở chỉnh sửa để bổ sung xác thực email và khai báo giao nhận theo quy trình mới.", "workflowVersion"); return issues; }
        if (!await HasSourceEmail(draftId, input, company, ct)) Add("SOURCE_EMAIL_REQUIRED", "Xác thực email cho Booking/DO/eDO hiện tại trước khi xác nhận. Mã xác thực có hiệu lực 30 phút sau khi xác nhận.", "source");
        if (input.Flow == "IMPORT")
        {
            if (input.Source.VesselName == "") Add("VESSEL_REQUIRED", "Bổ sung tên tàu trên lệnh giao hàng.", "source.vesselName");
            if (input.Source.VoyageNumber == "") Add("VOYAGE_REQUIRED", "Bổ sung chuyến tàu trên lệnh giao hàng.", "source.voyageNumber");
        }
        if (!HasCustomsConfirmation(draftId, input))
            Add("CUSTOMS_CONFIRMATION_REQUIRED", "Bấm Xác nhận thông quan cho danh sách container hiện tại trước khi xác nhận đăng ký.", "delivery.customsConfirmationScope");
        var delivery = input.Delivery;
        if (delivery.TransportMode == "TRUCK")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(delivery.TruckPlate, "^[A-Z0-9 .-]{5,20}$") || delivery.TruckPlate.Count(char.IsDigit) < 3) Add("TRUCK_PLATE_REQUIRED", "Bổ sung biển số xe giao nhận hợp lệ.", "delivery.truckPlate");
            if (delivery.DriverName == "") Add("DRIVER_REQUIRED", "Bổ sung tên tài xế.", "delivery.driverName");
            if (!System.Text.RegularExpressions.Regex.IsMatch(delivery.DriverPhone, @"^\+?[0-9 ().-]{7,30}$") || delivery.DriverPhone.Count(char.IsDigit) < 7) Add("DRIVER_PHONE_REQUIRED", "Bổ sung số điện thoại tài xế hợp lệ.", "delivery.driverPhone");
        }
        else if (delivery.BargeName == "" || delivery.BargeYear == null || delivery.BargeVoyage == "")
            Add("BARGE_REQUIRED", "Bổ sung tên sà lan, năm và số chuyến.", "delivery");
        if (delivery.OrderValidUntil != null && input.PlannedAt != null)
        {
            var plannedDate = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(input.PlannedAt, CultureInfo.InvariantCulture), PortZone).ToString("yyyy-MM-dd");
            if (string.CompareOrdinal(delivery.OrderValidUntil, plannedDate) < 0) Add("DELIVERY_ORDER_EXPIRED", "Hạn lệnh phải từ ngày dự kiến thực hiện trở đi.", "delivery.orderValidUntil");
            if (input.Flow == "IMPORT" && input.Source.ValidUntil != null && string.CompareOrdinal(delivery.OrderValidUntil, input.Source.ValidUntil) > 0)
                Add("DELIVERY_AFTER_EDO", "Hạn giao nhận không được vượt quá hạn DO/eDO.", "delivery.orderValidUntil");
        }
        var pickup = input.MainServiceId.HasValue && services.TryGetValue(input.MainServiceId.Value, out var main) && S(main, "code") == "IMPORT_PICKUP";
        foreach (var line in input.Lines)
        {
            if (!containers.TryGetValue(line.SourceContainerId, out var container)) continue;
            var iso = S(container, "typeCode").ToUpperInvariant();
            var cargo = S(container, "cargoType").ToLowerInvariant();
            operational.TryGetValue(line.SourceContainerId, out var state);
            var reefer = Equipment(container, state) == "REEFER" || (iso.Length > 2 && iso[2] == 'R') || iso.Contains("RF") || iso.Contains("RH") || cargo is "reefer" or "frozen";
            if (pickup && reefer && !line.Services.Any(usage => services.TryGetValue(usage.ServiceId, out var service) && S(service, "code") == "REEFER_POWER"))
                Add("IMPORT_REEFER_POWER_REQUIRED", "Container lạnh hàng nhập bắt buộc đăng ký cắm điện và hạn rút điện.", "lines.services", line.SourceContainerId);
            var declaration = line.Declaration;
            if (input.Flow == "EXPORT" && declaration == null)
                Add("CONTAINER_DECLARATION_REQUIRED", "Bổ sung khai báo container xuất trước khi xác nhận.", "lines.declaration", line.SourceContainerId);
            if (input.Flow == "EXPORT" && declaration != null)
            {
                if (declaration.ContainerNumber != S(container, "containerNumber") || declaration.IsoCode != S(container, "typeCode")
                    || declaration.SealNumber != S(container, "sealNumber") || declaration.WeightKg != container["vgmKg"]?.GetValue<decimal>())
                    Add("DECLARATION_SOURCE_MISMATCH", "Số container, ISO, seal và VGM phải khớp danh sách Booking đã công bố. Cập nhật hồ sơ nguồn trước nếu có thay đổi.", "lines.declaration", line.SourceContainerId);
                if (declaration.OperatorName == "" || declaration.CargoType == "" || declaration.DischargePort == "" || declaration.DestinationPort == "")
                    Add("CONTAINER_DECLARATION_REQUIRED", "Bổ sung hãng khai thác, loại hàng, cảng dỡ và cảng đích.", "lines.declaration", line.SourceContainerId);
                if ((cargo is "dangerous" or "dg") && (declaration.ImoClass == "" || declaration.UnNumber == ""))
                    Add("DG_DECLARATION_REQUIRED", "Container nguy hiểm cần có IMO và số UN.", "lines.declaration", line.SourceContainerId);
                if (reefer && declaration.Temperature == null) Add("TEMPERATURE_REQUIRED", "Container lạnh cần có nhiệt độ.", "lines.declaration", line.SourceContainerId);
                if (reefer && container["targetTemperature"] != null && declaration.Temperature != container["targetTemperature"]!.GetValue<decimal>())
                    Add("TEMPERATURE_SOURCE_MISMATCH", "Nhiệt độ phải khớp chỉ dẫn trên Booking đã công bố.", "lines.declaration.temperature", line.SourceContainerId);
                if ((cargo is "dangerous" or "dg") && (S(state, "unNumber") != declaration.UnNumber || S(state, "dgClass") != declaration.ImoClass))
                    Add("DG_SOURCE_MISMATCH", "IMO và UN phải khớp hồ sơ hàng nguy hiểm đã công bố.", "lines.declaration", line.SourceContainerId);
            }
            foreach (var usage in line.Services)
                if (usage.EndsAt != null && delivery.OrderValidUntil != null && DateTimeOffset.Parse(usage.EndsAt, CultureInfo.InvariantCulture)
                    > DateTimeOffset.Parse(delivery.OrderValidUntil + "T23:59:59+07:00", CultureInfo.InvariantCulture))
                    Add("SERVICE_AFTER_ORDER_EXPIRY", "Thời gian dịch vụ không được vượt quá hạn lệnh giao nhận.", "lines.services.endsAt", line.SourceContainerId);
        }
        return issues;
    }
}

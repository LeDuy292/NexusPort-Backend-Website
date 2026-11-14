using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceDraftService
{
    private static readonly TimeZoneInfo PortZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private static readonly Dictionary<string, string[]> StandardConditions = new()
    {
        ["EXPORT_DROPOFF"] = ["Booking hãng tàu đã được duyệt", "Container thuộc danh sách được công bố", "VGM hợp lệ và còn trong hạn CY cut-off", "Container và Booking không bị giữ hàng"],
        ["IMPORT_PICKUP"] = ["Có e-D/O đúng container và còn hiệu lực", "Container đã dỡ và đang trong bãi", "Bên đặt có quyền nhận hoặc được ủy quyền", "Container không bị giữ hàng"],
        ["REEFER_POWER"] = ["Container thuộc loại lạnh", "Container đang trong bãi", "Nhân viên bãi xác nhận tác nghiệp và thời gian cấp điện"],
        ["YARD_RELOCATION"] = ["Container đang trong bãi", "Container không bị giữ hàng", "Vị trí và thiết bị thực hiện do điều độ bố trí"],
        ["STORAGE_EXTENSION"] = ["Container đang lưu bãi và chưa ra cổng", "Yêu cầu được điều độ chấp thuận", "Gia hạn lưu bãi không tự gia hạn e-D/O hoặc CY cut-off"],
        ["CONTAINER_WEIGH"] = ["Container tại cổng hoặc trong bãi theo quy trình được phép", "Địa điểm và quy trình cân phải được cảng xác nhận"]
    };

    private Task<JsonObject?> FindRegistration(Guid draftId, CancellationToken ct)
        => FindRegistrationRow(draftId, ct);

    private async Task<JsonObject?> FindRegistrationRow(Guid draftId, CancellationToken ct)
    {
        var row = (await Rows("""
            SELECT id,draft_id AS "draftId",carrier_id AS "carrierId",number,draft_version AS "draftVersion",
                quote_token AS "quoteToken",status,payment_status AS "paymentStatus",currency,total,snapshot,
                confirmed_at AS "confirmedAt"
            FROM eport_service_registrations WHERE draft_id=@p0
            """, ct, draftId)).SingleOrDefault();
        if (row != null) await ApplyWorkflow(row, draftId, ct);
        return row;
    }

    private static JsonObject RegistrationDetail(JsonObject row)
    {
        var result = row["snapshot"]!.DeepClone().AsObject();
        foreach (var key in new[] { "id", "draftId", "number", "draftVersion", "status", "paymentStatus", "currency", "total", "confirmedAt", "approvalStatus", "orderVersion", "paymentReviewRequired", "pricingStatus" })
            result[key] = row[key]?.DeepClone();
        return result;
    }

    private static void AddRegistrationMetadata(JsonObject detail, JsonObject row)
    {
        detail["registrationId"] = row["id"]!.DeepClone();
        detail["registrationNumber"] = row["number"]!.DeepClone();
        foreach (var key in new[] { "status", "total", "currency", "paymentStatus", "confirmedAt", "approvalStatus", "orderVersion", "paymentReviewRequired", "pricingStatus" })
            detail[key] = row[key]?.DeepClone();
    }

    public async Task<JsonObject> Registration(Guid id, CancellationToken ct)
    {
        var company = await Company(ct);
        var row = await FindRegistration(id, ct);
        Require(row != null && G(row, "carrierId") == company, "Không tìm thấy đăng ký dịch vụ.", 404, "SERVICE_REGISTRATION_NOT_FOUND");
        return RegistrationDetail(row!);
    }

    public async Task<JsonObject> ValidateRegistration(Guid id, ServiceValidationInput request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var company = await Company(ct);
        var row = await OwnedDraft(id, company, request.ExpectedVersion, ct);
        var confirmed = await FindRegistration(id, ct);
        var validation = confirmed == null ? await BuildValidation(row, company, ct) : ConfirmedValidation(row, confirmed);
        await tx.CommitAsync(ct);
        return validation;
    }

    private async Task<JsonObject> OwnedDraft(Guid id, Guid company, int version, CancellationToken ct, bool locked = false)
    {
        var row = await Find(id, ct, locked);
        Require(row != null && G(row, "carrierId") == company, "Không tìm thấy hồ sơ dịch vụ.", 404, "SERVICE_DRAFT_NOT_FOUND");
        await EnsureWorkflowNotCancelled(id, ct);
        Require(version > 0 && row!["version"]!.GetValue<int>() == version,
            "Hồ sơ vừa thay đổi. Tải lại trước khi kiểm tra hoặc xác nhận.", 409, "SERVICE_DRAFT_VERSION_CONFLICT");
        return row!;
    }

    private static JsonObject ConfirmedValidation(JsonObject draft, JsonObject row) => new()
    {
        ["draftId"] = draft["id"]!.DeepClone(), ["draftVersion"] = draft["version"]!.DeepClone(),
        ["status"] = row["status"]!.DeepClone(), ["canConfirm"] = false,
        ["checkedAt"] = DateTimeOffset.UtcNow, ["currency"] = "VND", ["taxIncluded"] = false,
        ["total"] = row["total"]?.DeepClone(), ["subtotal"] = row["total"]?.DeepClone(),
        ["fees"] = row["snapshot"]!["fees"]!.DeepClone(), ["issues"] = row["snapshot"]!["issues"]!.DeepClone(),
        ["quoteToken"] = null, ["registration"] = RegistrationDetail(row)
    };

    private async Task<List<JsonObject>> OperationalRows(Guid company, ServiceDraftInput input, CancellationToken ct)
    {
        var ids = input.Lines.Select(line => line.SourceContainerId).ToArray();
        if (ids.Length == 0) return [];
        if (input.Flow == "EXPORT") return DemoCustoms(await Rows("""
            SELECT e.id AS "sourceContainerId",c.status::text AS "physicalStatus",t.size::text AS "typeSize",
                t.category::text AS "typeCategory",e.customs_cleared AS "customsCleared",e.un_number AS "unNumber",e.dg_class AS "dgClass",
                (e.id::text LIKE '03810000-%' AND b.booking_no LIKE 'DEMO-EPORT-%' AND e.container_no LIKE 'NXPU%') AS "isDemoSource",
                EXISTS(SELECT 1 FROM container_visits y WHERE y.container_id=e.container_id AND y.declaration_id=e.declaration_id
                       AND y.visit_status='active' AND y.started_at IS NOT NULL AND y.completed_at IS NULL) AS "hasYardStay"
            FROM export_booking_containers e JOIN export_bookings b ON b.id=e.export_booking_id
            JOIN containers c ON c.id=e.container_id JOIN container_types t ON t.id=c.container_type_id
            WHERE e.id=ANY(@p0) AND b.carrier_id=@p1 AND b.id=@p2
            ORDER BY e.id
            """, ct, ids, company, input.Source.BookingId));
        return DemoCustoms(await Rows("""
            SELECT v.id AS "sourceContainerId",c.status::text AS "physicalStatus",t.size::text AS "typeSize",
                t.category::text AS "typeCategory",NULL::boolean AS "customsCleared",
                (v.id::text ~ '^e911110[01]-' AND c.container_no LIKE 'DEMU%'
                 AND to_jsonb(v)->>'source_reference' IN ('DEMO-EPORT-IMPORT-20261111','DEMO-EPORT-IMPORT-20261111-CARRIER01')) AS "isDemoSource",
                (v.visit_status='active' AND v.started_at IS NOT NULL AND v.completed_at IS NULL) AS "hasYardStay"
            FROM container_visits v JOIN containers c ON c.id=v.container_id
            JOIN container_types t ON t.id=c.container_type_id
            JOIN container_declarations d ON d.id=v.declaration_id AND d.container_id=v.container_id
            WHERE v.id=ANY(@p0) AND (d.carrier_id=@p1 OR EXISTS(
                SELECT 1 FROM container_visit_access a WHERE a.container_visit_id=v.id AND a.carrier_id=@p1))
            ORDER BY v.id
            """, ct, ids, company));
    }

    private static DateTimeOffset? At(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    private static string Normal(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");
    private static string Size(JsonObject container, JsonObject? operational)
    {
        var size = Normal(S(operational, "typeSize"));
        if (size is "ft20" or "20ft" or "20") return "20FT";
        if (size is "ft40" or "40ft" or "40") return "40FT";
        if (size is "ft45" or "45ft" or "45") return "45FT";
        // ISO 45G0 denotes 40ft high-cube. Never treat the first two digits as feet.
        var code = S(container, "typeCode").ToUpperInvariant();
        if (Regex.IsMatch(code, "^[245L][0-9A-Z][GPRTUV][0-9A-Z]$"))
            return code[0] switch { '2' => "20FT", '4' => "40FT", 'L' => "45FT", _ => "" };
        return "";
    }
    private static string Equipment(JsonObject container, JsonObject? operational)
    {
        var cargo = Normal(S(container, "cargoType"));
        var category = Normal(S(operational, "typeCategory"));
        if (cargo is "dangerous" or "dg") return "DG";
        if (cargo is "oversized" or "overweight" or "oog") return "OOG";
        if (category is "reefer" || cargo is "reefer" or "frozen") return "REEFER";
        if (category is "opentop" or "flatrack") return "OOG";
        if (category is "dry" or "tank" || cargo is "general" or "perishable") return "DRY";
        return "";
    }

    private async Task<JsonObject> BuildValidation(JsonObject draft, Guid company, CancellationToken ct)
    {
        var input = draft["content"]!.Deserialize<ServiceDraftInput>(Json)!;
        var issues = new JsonArray();
        var fees = new JsonArray();
        var needsApproval = true;
        Require((await Rows("SELECT to_regclass('eport_service_bills') IS NOT NULL AS ready", ct))[0]["ready"]!.GetValue<bool>(), "Cần áp dụng migration 20261112_042 cho luồng Dispatcher lập phí.", 503, "SERVICE_BILLING_SCHEMA_REQUIRED");
        var deliverySchema = await DeliverySchema(ct);
        var newDelivery = deliverySchema && input.WorkflowVersion == 2;
        void Issue(string code, string message, string field, Guid? container = null, Guid? service = null, bool warning = false)
            => issues.Add(new JsonObject { ["code"] = code, ["message"] = message, ["field"] = field,
                ["sourceContainerId"] = container?.ToString(), ["serviceId"] = service?.ToString(), ["severity"] = warning ? "WARNING" : "ERROR" });
        var now = DateTimeOffset.UtcNow;
        var planned = At(input.PlannedAt);
        if (!input.MainServiceId.HasValue) Issue("MAIN_SERVICE_REQUIRED", "Chọn dịch vụ chính.", "mainServiceId");
        if (input.Lines.Length == 0) Issue("CONTAINERS_REQUIRED", "Chọn ít nhất một container có hàng.", "lines");
        if (planned == null) Issue("PLANNED_AT_REQUIRED", "Bổ sung thời gian dự kiến thực hiện.", "plannedAt");
        else if (planned < now) Issue("PLANNED_AT_PAST", "Thời gian dự kiến đã qua; cập nhật trước khi xác nhận.", "plannedAt");
        if (string.IsNullOrWhiteSpace(input.ContactName)) Issue("CONTACT_NAME_REQUIRED", "Bổ sung người liên hệ.", "contactName");
        if (!Regex.IsMatch(input.ContactPhone, @"^\+?[0-9 ().-]{7,30}$") || input.ContactPhone.Count(char.IsDigit) < 7)
            Issue("CONTACT_PHONE_REQUIRED", "Bổ sung số điện thoại liên hệ hợp lệ.", "contactPhone");
        var source = new JsonObject { ["verificationStatus"] = "PENDING_SOURCE" };
        var sourceAvailable = input.Flow == "IMPORT";
        if (input.Flow == "EXPORT")
        {
            if (input.Source.BookingId == null) Issue("BOOKING_REQUIRED", "Chọn Booking được cảng tiếp nhận và có danh sách container công bố.", "source.bookingId");
            else try
            {
                source = await ExportSource(company, input.Source.BookingId.Value, ct);
                sourceAvailable = true;
                var receive = At(S(source, "receiveFrom"));
                var cutoff = At(S(source, "cyCutoff"));
                if (cutoff == null || cutoff <= now) Issue("CY_CUTOFF_CLOSED", "Chuyến tàu đã hết hạn CY cut-off hoặc chưa có hạn hợp lệ.", "source.bookingId");
                if (planned != null && ((receive != null && planned < receive) || (cutoff != null && planned > cutoff)))
                    Issue("OUTSIDE_RECEIVING_WINDOW", "Thời gian dự kiến phải nằm trong khoảng tiếp nhận đến CY cut-off của chuyến tàu.", "plannedAt");
            }
            catch (CatalogException error) when (error.StatusCode < 500) { Issue("SOURCE_UNAVAILABLE", error.Message, "source.bookingId"); }
        }
        else
        {
            source = new JsonObject { ["documentType"] = input.Source.DocumentType, ["referenceNumber"] = input.Source.ReferenceNumber, ["verificationStatus"] = "UNVERIFIED" };
            source["vesselName"] = input.Source.VesselName; source["voyageNumber"] = input.Source.VoyageNumber;
            if (newDelivery) { source["verificationStatus"] = "PORT_DOCUMENT_REVIEW_REQUIRED"; needsApproval = true; }
            Issue("EDO_UNVERIFIED", newDelivery ? "DO/eDO cần được nhân viên cảng đối chiếu chứng từ và quyền nhận hàng trước khi duyệt, thanh toán. Email OTP không xác nhận hiệu lực lệnh." : "DO/eDO mới là thông tin khai báo, chưa có nguồn cấp lệnh được xác minh và liên kết đúng container. Chưa thể xác nhận đăng ký hàng nhập.", "source.referenceNumber", warning: newDelivery);
            if (string.IsNullOrWhiteSpace(input.Source.ReferenceNumber)) Issue("EDO_REFERENCE_REQUIRED", "Bổ sung số DO/eDO.", "source.referenceNumber");
            if (string.IsNullOrWhiteSpace(input.Source.BlNumber)) Issue("BL_REQUIRED", "Bổ sung số vận đơn.", "source.blNumber");
            if (input.Source.ValidUntil == null) Issue("EDO_EXPIRY_REQUIRED", "Bổ sung hạn hiệu lực DO/eDO.", "source.validUntil");
            else if (string.CompareOrdinal(input.Source.ValidUntil, TimeZoneInfo.ConvertTime(planned ?? now, PortZone).ToString("yyyy-MM-dd")) < 0)
                Issue("EDO_EXPIRED", "DO/eDO hết hạn trước thời gian dự kiến thực hiện.", "source.validUntil");
        }
        List<JsonObject> containers = [];
        if (sourceAvailable && input.Lines.Length > 0)
            try { containers = await ContainerRows(company, input.Flow, input.Source.BookingId, "", input.Lines.Select(line => line.SourceContainerId).ToArray(), ct); }
            catch (CatalogException error) { Issue("CONTAINERS_UNAVAILABLE", error.Message, "lines"); }
        var operational = await OperationalRows(company, input, ct);
        var serviceIds = input.Lines.SelectMany(line => line.Services).Select(usage => usage.ServiceId)
            .Concat(input.MainServiceId.HasValue ? [input.MainServiceId.Value] : Array.Empty<Guid>()).Distinct().Order().ToArray();
        var services = serviceIds.Length == 0 ? [] : await Rows("""
            SELECT id,code,name,direction,unit,is_active AS "isActive",requires_approval AS "requiresApproval",conditions,version
            FROM eport_services WHERE id=ANY(@p0) ORDER BY id
            """, ct, serviceIds);
        var byService = services.ToDictionary(row => G(row, "id"));
        var byContainer = containers.ToDictionary(row => G(row, "sourceContainerId"));
        var byOperational = operational.ToDictionary(row => G(row, "sourceContainerId"));
        foreach (var serviceId in serviceIds)
        {
            if (!byService.TryGetValue(serviceId, out var service)) { Issue("SERVICE_NOT_FOUND", "Dịch vụ không còn tồn tại.", "lines.services", service: serviceId); continue; }
            if (!service["isActive"]!.GetValue<bool>()) Issue("SERVICE_SUSPENDED", $"{S(service, "name")}: dịch vụ đang tạm ngừng.", "lines.services", service: serviceId);
            if (S(service, "direction") != "BOTH" && S(service, "direction") != input.Flow) Issue("SERVICE_DIRECTION", "Dịch vụ không phù hợp luồng hàng.", "lines.services", service: serviceId);
            var conditions = service["conditions"]!.AsArray().Select(node => node!.GetValue<string>()).ToHashSet();
            if (!StandardConditions.TryGetValue(S(service, "code"), out var standard) || !conditions.SetEquals(standard))
            {
                needsApproval = true;
                Issue("MANUAL_CONDITIONS", $"{S(service, "name")}: các điều kiện riêng cần được cảng đối chiếu trước khi thực hiện.", "lines.services", service: serviceId, warning: true);
            }
            if (service["requiresApproval"]!.GetValue<bool>() || S(service, "code") is "STORAGE_EXTENSION" or "YARD_RELOCATION" or "REEFER_POWER" or "CONTAINER_WEIGH")
            {
                needsApproval = true;
                Issue("PORT_APPROVAL_REQUIRED", $"{S(service, "name")}: cần cảng xác nhận điều kiện/kế hoạch tác nghiệp.", "lines.services", service: serviceId, warning: true);
            }
        }
        foreach (var line in input.Lines)
        {
            if (!byContainer.TryGetValue(line.SourceContainerId, out var container))
            { Issue("CONTAINER_UNAVAILABLE", "Container không còn thuộc nguồn được phép của công ty hoặc không còn đủ trạng thái đăng ký.", "lines", line.SourceContainerId); continue; }
            byOperational.TryGetValue(line.SourceContainerId, out var state);
            var number = S(container, "containerNumber");
            var physical = Normal(S(state, "physicalStatus"));
            if (state == null || string.IsNullOrEmpty(physical)) Issue("CONTAINER_STATE_UNKNOWN", $"{number}: chưa xác định được trạng thái container.", "lines", line.SourceContainerId);
            else if (physical is "hold" or "customshold" or "inactive" or "blocked" or "damagedunsafe")
                Issue("CONTAINER_ON_HOLD", $"{number}: container đang bị giữ, khóa hoặc không an toàn.", "lines", line.SourceContainerId);
            if (container["hasActiveOrder"]?.GetValue<bool>() == true)
                Issue("CONTAINER_ACTIVE_ORDER", $"{number}: container đang có một lệnh dịch vụ mở. Mỗi container chỉ có một lệnh đang mở.", "lines", line.SourceContainerId);
            if (input.Flow == "EXPORT")
            {
                if (!(container["vgmKg"]?.GetValue<decimal>() > 0)) Issue("VGM_REQUIRED", $"{number}: thiếu VGM hợp lệ từ hồ sơ nguồn.", "lines", line.SourceContainerId);
                if (string.IsNullOrWhiteSpace(S(container, "sealNumber"))) Issue("SEAL_REQUIRED", $"{number}: thiếu seal từ hồ sơ nguồn.", "lines", line.SourceContainerId);
                if (state?["customsCleared"]?.GetValue<bool>() != true && !HasCustomsConfirmation(G(draft, "id"), input))
                    Issue("CUSTOMS_BEFORE_GATE", $"{number}: chưa ghi nhận thông quan; bắt buộc kiểm tra lại trước khi qua cổng/xếp tàu.", "lines", line.SourceContainerId, warning: true);
            }
            var size = Size(container, state);
            var equipment = Equipment(container, state);
            if (size == "" || equipment == "") Issue("EQUIPMENT_UNKNOWN", $"{number}: chưa xác định đủ kích thước/nhóm container để chọn biểu phí.", "lines", line.SourceContainerId);
            if (!line.Services.Any(usage => usage.ServiceId == input.MainServiceId)) Issue("MAIN_SERVICE_REQUIRED", $"{number}: thiếu dịch vụ chính.", "lines.services", line.SourceContainerId);
            foreach (var usage in line.Services)
            {
                if (!byService.TryGetValue(usage.ServiceId, out var service)) continue;
                var code = S(service, "code");
                var unit = S(service, "unit");
                if (usage.ServiceId != input.MainServiceId && code is "EXPORT_DROPOFF" or "IMPORT_PICKUP")
                    Issue("INVALID_ADDON", "Dịch vụ giao nhận chỉ được chọn làm dịch vụ chính.", "lines.services", line.SourceContainerId, usage.ServiceId);
                if (code == "REEFER_POWER" && (ClearlyNonReefer(container) || Normal(S(state, "typeCategory")) != "reefer"))
                    Issue("REEFER_REQUIRED", $"{number}: cắm điện chỉ dành cho thiết bị container lạnh.", "lines.services", line.SourceContainerId, usage.ServiceId);
                var start = At(usage.StartsAt);
                var end = At(usage.EndsAt);
                var inYard = state?["hasYardStay"]?.GetValue<bool>() == true;
                if (code is "REEFER_POWER" or "YARD_RELOCATION" or "STORAGE_EXTENSION" or "IMPORT_PICKUP")
                {
                    if (!inYard)
                    {
                        var plannedAfterDropoff = input.Flow == "EXPORT" && input.MainServiceId.HasValue
                            && byService.TryGetValue(input.MainServiceId.Value, out var main) && S(main, "code") == "EXPORT_DROPOFF" && usage.ServiceId != input.MainServiceId;
                        Issue(plannedAfterDropoff ? "IN_YARD_PENDING" : "IN_YARD_REQUIRED",
                            $"{number}: chỉ thực hiện {S(service, "name")} sau khi ghi nhận đúng lượt container đã vào bãi.", "lines.services", line.SourceContainerId, usage.ServiceId, plannedAfterDropoff);
                        if (plannedAfterDropoff) needsApproval = true;
                    }
                    if (start != null && planned != null && start < planned)
                        Issue("USAGE_BEFORE_PLANNED", "Thời gian dịch vụ không được trước thời gian thực hiện dự kiến.", "lines.services.startsAt", line.SourceContainerId, usage.ServiceId, warning: true);
                }
                decimal? quantity = usage.Quantity;
                if (unit == "CONTAINER") quantity = 1;
                if (unit is "HOUR" or "DAY")
                {
                    quantity = null;
                    if (start == null || end == null || end <= start) Issue("USAGE_TIME_REQUIRED", $"{number} — {S(service, "name")}: bổ sung giờ bắt đầu và kết thúc hợp lệ.", "lines.services", line.SourceContainerId, usage.ServiceId);
                    else
                    {
                        var hours = (decimal)(end.Value - start.Value).Ticks / TimeSpan.TicksPerHour;
                        quantity = unit == "HOUR" ? decimal.Ceiling(hours * 100) / 100 : decimal.Ceiling(hours / 24);
                    }
                }
                if (start != null && start < now) Issue("USAGE_TIME_PAST", $"{number}: thời gian bắt đầu dịch vụ đã qua.", "lines.services.startsAt", line.SourceContainerId, usage.ServiceId);
                if (quantity == null || quantity <= 0 || quantity > 10000 || (unit != "HOUR" && quantity != decimal.Truncate(quantity.Value)))
                    Issue("QUANTITY_REQUIRED", $"{number} — {S(service, "name")}: cần số đơn vị hợp lệ, tối đa 10.000.", "lines.services.quantity", line.SourceContainerId, usage.ServiceId);

            }
        }
        if (deliverySchema)
            foreach (var issue in await DeliveryIssues(G(draft, "id"), input, company, byContainer, byService, byOperational, ct)) issues.Add(issue!.DeepClone());
        else if (input.WorkflowVersion == 2) Issue("DELIVERY_SCHEMA_REQUIRED", "Cần triển khai migration 20261110_041 để xác thực email và đăng ký giao nhận.", "workflowVersion");
        foreach (var issue in await FleetIssues(G(draft, "id"), input, company, source, ct)) issues.Add(issue!.DeepClone());
        var canConfirm = !issues.Any(issue => S(issue, "severity") == "ERROR");
        var validation = new JsonObject
        {
            ["draftId"] = draft["id"]!.DeepClone(), ["draftVersion"] = draft["version"]!.DeepClone(),
            ["status"] = canConfirm ? "READY" : "INCOMPLETE", ["canConfirm"] = canConfirm,
            ["pricingMode"] = "DISPATCHER", ["pricingStatus"] = "AWAITING",
            ["currency"] = "VND", ["taxIncluded"] = false, ["subtotal"] = null,
            ["total"] = null,
            ["issues"] = issues, ["fees"] = fees, ["requiresApproval"] = needsApproval,
            ["content"] = draft["content"]!.DeepClone(), ["sourceSnapshot"] = source,
            ["mainServiceName"] = input.MainServiceId.HasValue && byService.TryGetValue(input.MainServiceId.Value, out var selectedMain) ? S(selectedMain, "name") : null,
            ["containers"] = new JsonArray(containers.Select(row => (JsonNode)row.DeepClone()).ToArray()),
            ["serviceSnapshots"] = new JsonArray(services.Select(row => (JsonNode)row.DeepClone()).ToArray()),
            ["operationalSnapshots"] = new JsonArray(operational.Select(row => (JsonNode)row.DeepClone()).ToArray())
        };
        // This token binds the reviewed submission and source conditions, not a price.
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(validation.ToJsonString())));
        validation["quoteToken"] = canConfirm ? token : null;
        validation["checkedAt"] = now;
        validation["registration"] = null;
        return validation;
    }

    private async Task LockConfirmationInputs(JsonObject draft, Guid company, CancellationToken ct)
    {
        var input = draft["content"]!.Deserialize<ServiceDraftInput>(Json)!;
        if (input.Flow == "EXPORT") await EnsureExportSourceSchema(ct);
        var ids = input.Lines.Select(line => line.SourceContainerId).ToArray();
        var serviceIds = input.Lines.SelectMany(line => line.Services).Select(usage => usage.ServiceId)
            .Concat(input.MainServiceId.HasValue ? [input.MainServiceId.Value] : Array.Empty<Guid>()).Distinct().Order().ToArray();
        if (serviceIds.Length > 0)
        {
            await Rows("SELECT id FROM eport_services WHERE id=ANY(@p0) ORDER BY id FOR SHARE", ct, serviceIds);
        }
        if (ids.Length == 0) return;
        if (input.Flow == "EXPORT")
            await Rows("""
                SELECT e.id FROM export_booking_containers e JOIN export_bookings b ON b.id=e.export_booking_id
                JOIN export_voyages v ON v.id=b.voyage_id JOIN container_declarations d ON d.id=e.declaration_id
                JOIN containers c ON c.id=e.container_id JOIN container_types t ON t.id=c.container_type_id
                WHERE e.id=ANY(@p0) AND b.carrier_id=@p1 ORDER BY e.id FOR SHARE OF b,v,e,d,c,t
                """, ct, ids, company);
        else
        {
            await Rows("""
                SELECT v.id FROM container_visits v JOIN containers c ON c.id=v.container_id
                JOIN container_types t ON t.id=c.container_type_id JOIN container_declarations d ON d.id=v.declaration_id
                WHERE v.id=ANY(@p0) AND (d.carrier_id=@p1 OR EXISTS(SELECT 1 FROM container_visit_access a WHERE a.container_visit_id=v.id AND a.carrier_id=@p1))
                ORDER BY v.id FOR SHARE OF v,c,t,d
                """, ct, ids, company);
            await Rows("SELECT container_visit_id FROM container_visit_access WHERE carrier_id=@p0 AND container_visit_id=ANY(@p1) FOR SHARE", ct, company, ids);
        }
    }

    public async Task<JsonObject> ConfirmRegistration(Guid id, ServiceConfirmationInput request, CancellationToken ct)
    {
        Require(!string.IsNullOrWhiteSpace(request.QuoteToken) && Regex.IsMatch(request.QuoteToken, "^[A-Fa-f0-9]{64}$"),
            "Kiểm tra hồ sơ trước khi gửi Dispatcher.", 422, "SERVICE_QUOTE_REQUIRED");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Rows("SELECT id FROM users WHERE id=@p0 FOR SHARE", ct, Actor);
        await Rows("SELECT carrier_id FROM carrier_users WHERE user_id=@p0 FOR SHARE", ct, Actor);
        var company = await Company(ct);
        await Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))", ct, "eport-service-draft:" + id.ToString("N"));
        var draft = await OwnedDraft(id, company, request.ExpectedVersion, ct, true);
        var existing = await FindRegistration(id, ct);
        if (existing != null)
        {
            Require(string.Equals(S(existing, "quoteToken"), request.QuoteToken, StringComparison.OrdinalIgnoreCase),
                "Hồ sơ đã xác nhận. Mở lại đơn để xem đơn giá đã lưu.", 409, "SERVICE_REGISTRATION_LOCKED");
            await tx.CommitAsync(ct);
            return RegistrationDetail(existing);
        }
        await LockConfirmationInputs(draft, company, ct);
        var validation = await BuildValidation(draft, company, ct);
        Require(validation["canConfirm"]!.GetValue<bool>(), "Hồ sơ chưa đủ điều kiện. Kiểm tra lại các thông tin còn thiếu.", 422, "SERVICE_REGISTRATION_INCOMPLETE");
        Require(string.Equals(S(validation, "quoteToken"), request.QuoteToken, StringComparison.OrdinalIgnoreCase),
            "Hồ sơ nguồn, điều kiện hoặc biểu phí đã thay đổi. Kiểm tra lại và xem số tiền mới trước khi xác nhận.", 409, "SERVICE_QUOTE_CHANGED");
        // Registration contains no charge. The separate, immutable dispatcher bill
        // is the only payment authority for this workflow (migration 042).
        decimal? total = null;
        var snapshot = validation.DeepClone().AsObject();
        foreach (var key in new[] { "canConfirm", "quoteToken", "registration", "checkedAt", "status" }) snapshot.Remove(key);
        snapshot["draftNumber"] = draft["number"]!.DeepClone();
        var status = "PENDING_APPROVAL";
        var regNumber = "DV-" + id.ToString("N").ToUpperInvariant();
        await Exec("""
            INSERT INTO eport_service_registrations(id,draft_id,carrier_id,confirmed_by,number,draft_version,quote_token,status,payment_status,total,snapshot)
            VALUES(@p0,@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,CAST(@p9 AS jsonb))
            """, ct, id, company, Actor, regNumber, request.ExpectedVersion, S(validation, "quoteToken"),
            status, "UNPAID", total, snapshot.ToJsonString());
        if ((await Rows("SELECT to_regclass('port.event_outbox') IS NOT NULL AS ready", ct))[0]["ready"]!.GetValue<bool>())
        {
            var bookingRef = S(snapshot["sourceSnapshot"], "bookingNumber");
            if (string.IsNullOrEmpty(bookingRef)) bookingRef = S(snapshot["content"]?["source"], "referenceNumber");
            var outboxPayload = new JsonObject
            {
                ["eventId"] = Guid.NewGuid().ToString(),
                ["eventType"] = "ServiceOrderSubmitted",
                ["version"] = request.ExpectedVersion,
                ["orderId"] = id.ToString(),
                ["registrationNumber"] = regNumber,
                ["carrierId"] = company.ToString(),
                ["flow"] = S(draft, "flow"),
                ["bookingOrEdo"] = bookingRef,
                ["submittedAt"] = DateTimeOffset.UtcNow
            };
            await Exec("""
                INSERT INTO port.event_outbox("Id","EventType","BookingNo","PayloadJson","Status","Attempts","CreatedAt")
                VALUES(@p0,'ServiceOrderSubmitted',@p1,@p2,0,0,now())
                """, ct, Guid.NewGuid(), bookingRef, outboxPayload.ToJsonString());
        }
        var saved = await FindRegistration(id, ct);
        await tx.CommitAsync(ct);
        return RegistrationDetail(saved!);
    }
}

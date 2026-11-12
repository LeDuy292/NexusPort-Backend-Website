using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceOrderService
{
    public async Task<JsonObject> RegistrationReceipt(Guid id, CancellationToken ct, int? expectedVersion = null, Guid? sourceContainerId = null)
    {
        // Read one consistent handling/settlement version. The confirmed content
        // and fees come from the immutable registration, never today's catalog.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var company = await OrderAccess(ct);
        var order = await OrderRow(id, company, ct);
        Require(order["registrationId"] != null, "Chỉ hồ sơ đã xác nhận mới có phiếu đăng ký dịch vụ.", 409, "SERVICE_RECEIPT_NOT_CONFIRMED");
        Require(!company.HasValue || S(order,"pricingMode")!="DISPATCHER" || S(order,"paymentStatus")=="PAID" || S(order,"pricingStatus")=="PUBLISHED"&&S(order,"paymentStatus")=="NOT_REQUIRED",
            "Chứng từ sẽ có sau thanh toán hoặc sau khi Dispatcher xác nhận không phát sinh tiền.",409,"SERVICE_RECEIPT_PAYMENT_REQUIRED");
        Require(!expectedVersion.HasValue || expectedVersion > 0 && expectedVersion == order["version"]!.GetValue<int>(),
            "Trạng thái đơn đã thay đổi sau khi mở phiếu. Tải lại bản xem trước trước khi xuất PDF.", 409, "SERVICE_RECEIPT_CHANGED");
        var receipt = new JsonObject
        {
            ["templateVersion"] = "1.1", ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["timeZone"] = "Asia/Ho_Chi_Minh", ["taxIncluded"] = false,
            ["notice"] = "Phiếu ghi nhận đăng ký dịch vụ; không thay thế hóa đơn, lệnh giao hàng hoặc giấy phép qua cổng."
        };
        foreach (var key in new[] { "id", "number", "registrationId", "draftNumber", "flow", "status", "approvalStatus",
            "paymentStatus", "paymentReviewRequired", "companyName", "mainServiceName", "sourceLabel", "sourceSnapshot",
            "confirmedAt", "updatedAt", "currency", "total" }) receipt[key] = order[key]?.DeepClone();
        receipt["orderVersion"] = order["version"]!.DeepClone();
        receipt["customsStatus"] = order["customsStatus"]?.DeepClone();
        if (S(order, "customsStatus") == "DEMO_CLEARED")
            receipt["notice"] = "DEMO - thông quan mô phỏng để thử đăng ký dịch vụ. Phiếu không phải kết quả hải quan, hóa đơn hoặc giấy phép qua cổng thực tế.";
        if (S(order, "customsStatus") == "CUSTOMER_CONFIRMED")
            receipt["notice"] = "Khách hàng đã xác nhận thông quan trong hồ sơ dịch vụ. Phiếu không thay thế kết quả hải quan, hóa đơn hoặc giấy phép qua cổng.";
        var content = new JsonObject();
        foreach (var key in new[] { "plannedAt", "contactName", "contactPhone", "note", "source", "delivery", "invoiceBuyer", "workflowVersion" })
            content[key] = order["content"]?[key]?.DeepClone();
        receipt["content"] = content;
        receipt["containers"] = SelectReceiptFields(order["containers"],
            ["sourceContainerId", "containerId", "containerNumber", "typeCode", "loadStatus", "cargoType", "sealNumber", "vgmKg", "targetTemperature"]);
        foreach (var container in receipt["containers"]!.AsArray())
        {
            var line = order["content"]?["lines"]?.AsArray().FirstOrDefault(item => item?["sourceContainerId"]?.ToString() == container?["sourceContainerId"]?.ToString());
            container!["customsSealRequired"] = line?["customsSealRequired"]?.DeepClone() ?? JsonValue.Create(false);
            container["declaration"] = line?["declaration"]?.DeepClone();
        }
        receipt["fees"] = SelectReceiptFields(order["fees"],
            ["sourceContainerId", "containerNumber", "serviceName", "serviceCode", "unit", "quantity", "unitPrice", "amount",
                "size", "equipment", "pricingDate", "startsAt", "endsAt", "billingNote"]);
        receipt["scope"] = sourceContainerId.HasValue ? "CONTAINER" : "ORDER";
        receipt["orderTotal"] = receipt["total"]?.DeepClone();
        if (sourceContainerId.HasValue)
        {
            var selected = receipt["containers"]!.AsArray().FirstOrDefault(row =>
                Guid.TryParse(row?["sourceContainerId"]?.ToString(), out var containerId) && containerId == sourceContainerId.Value);
            Require(selected != null, "Container không thuộc hồ sơ đăng ký này.", 404, "SERVICE_RECEIPT_CONTAINER_NOT_FOUND");
            receipt["selectedContainerId"] = sourceContainerId.Value.ToString();
            receipt["selectedContainerNumber"] = selected!["containerNumber"]?.DeepClone();
            receipt["containers"] = new JsonArray(selected.DeepClone());
            var selectedFees = receipt["fees"]!.AsArray().Where(row =>
                Guid.TryParse(row?["sourceContainerId"]?.ToString(), out var containerId) && containerId == sourceContainerId.Value).ToArray();
            receipt["total"] = S(order,"pricingMode")=="DISPATCHER"&&selectedFees.Length==0 ? null : JsonValue.Create(selectedFees.Sum(row => row!["amount"]!.GetValue<decimal>()));
            receipt["fees"] = new JsonArray(selectedFees.Select(row => row!.DeepClone()).ToArray());
        }
        var reasons = await Rows("""
            SELECT action,reason,created_at AS "createdAt" FROM eport_service_order_events
            WHERE draft_id=@p0 AND action IN ('CANCEL','REJECT','PAYMENT_REVIEW_REQUIRED')
            ORDER BY created_at DESC,id DESC LIMIT 3
            """, ct, id);
        receipt["statusNotes"] = new JsonArray(reasons.Select(row => (JsonNode)row.DeepClone()).ToArray());
        await tx.CommitAsync(ct);
        return receipt;
    }

    private static JsonArray SelectReceiptFields(JsonNode? source, string[] keys)
        => new((source?.AsArray() ?? new JsonArray()).Select(item =>
        {
            var row = new JsonObject();
            foreach (var key in keys) row[key] = item?[key]?.DeepClone();
            return (JsonNode)row;
        }).ToArray());
}

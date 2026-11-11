using System.Text.Json.Nodes;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceDraftService
{
    private async Task ApplyWorkflowRows(List<JsonObject> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var available = await Rows("SELECT to_regclass('eport_service_order_states') IS NOT NULL AS available", ct);
        if (!available[0]["available"]!.GetValue<bool>()) return;
        var states = await Rows("""
            SELECT o.draft_id AS id,o.status,o.approval_status AS "approvalStatus",o.payment_status AS "paymentStatus",o.version AS "orderVersion",
             EXISTS(SELECT 1 FROM eport_service_order_events e WHERE e.draft_id=o.draft_id AND e.action='PAYMENT_REVIEW_REQUIRED') AS "paymentReviewRequired"
            FROM eport_service_order_states o WHERE o.draft_id=ANY(@p0)
            """, ct, rows.Select(row => G(row, "id")).ToArray());
        var indexed = states.ToDictionary(row => G(row, "id"));
        foreach (var row in rows)
            if (indexed.TryGetValue(G(row, "id"), out var state))
                foreach (var key in new[] { "status", "approvalStatus", "paymentStatus", "orderVersion", "paymentReviewRequired" })
                    row[key] = state[key]?.DeepClone();
    }

    // A rolling deployment may still serve the old drafts/catalog before 040 is
    // applied. Once present, every old endpoint observes the same workflow lock.
    private async Task<JsonObject?> WorkflowState(Guid id, CancellationToken ct)
    {
        var available = await Rows("SELECT to_regclass('eport_service_order_states') IS NOT NULL AS available", ct);
        if (!available[0]["available"]!.GetValue<bool>()) return null;
        return (await Rows("""
            SELECT o.status,o.approval_status AS "approvalStatus",o.payment_status AS "paymentStatus",o.version AS "orderVersion",o.updated_at AS "workflowUpdatedAt",
             EXISTS(SELECT 1 FROM eport_service_order_events e WHERE e.draft_id=o.draft_id AND e.action='PAYMENT_REVIEW_REQUIRED') AS "paymentReviewRequired"
            FROM eport_service_order_states o WHERE o.draft_id=@p0
            """, ct, id)).SingleOrDefault();
    }

    private async Task EnsureDraftWorkflowOpen(Guid id, CancellationToken ct)
    {
        var workflow = await WorkflowState(id, ct);
        Require(workflow == null || S(workflow, "status") == "DRAFT", "Hồ sơ đã được xác nhận hoặc đã hủy; không thể sửa bản nháp.", 409, "SERVICE_REGISTRATION_LOCKED");
    }

    private async Task EnsureWorkflowNotCancelled(Guid id, CancellationToken ct)
    {
        var workflow = await WorkflowState(id, ct);
        Require(workflow == null || S(workflow, "status") is not ("CANCELLED" or "REJECTED"), "Hồ sơ đã hủy hoặc bị từ chối; không thể kiểm tra hoặc xác nhận lại.", 409, "SERVICE_ORDER_STATE_CONFLICT");
    }

    private async Task ApplyWorkflow(JsonObject row, Guid id, CancellationToken ct)
    {
        var workflow = await WorkflowState(id, ct);
        if (workflow == null) return;
        foreach (var key in new[] { "status", "approvalStatus", "paymentStatus", "orderVersion", "workflowUpdatedAt", "paymentReviewRequired" })
            row[key] = workflow[key]?.DeepClone();
    }
}

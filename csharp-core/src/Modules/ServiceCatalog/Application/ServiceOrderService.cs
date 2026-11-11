using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

/// <summary>Mutable handling/settlement with immutable confirmed prices and append-only history.</summary>
public sealed partial class ServiceOrderService(AppDbContext db, ICurrentUser user, IPayOsGateway gateway)
{
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
    private static void Require(bool valid, string message, int status = 422, string code = "SERVICE_ORDER_VALIDATION")
        => ServiceDraftRules.Require(valid, message, status, code);

    // A JWT role alone never grants access: verify the current persisted account.
    private async Task<Guid?> OrderAccess(CancellationToken ct)
    {
        var locked = db.Database.CurrentTransaction != null;
        var accounts = await Rows("SELECT role FROM users WHERE id=@p0 AND is_active" + (locked ? " FOR SHARE" : ""), ct, Actor);
        var role = accounts.FirstOrDefault()?["role"]?.GetValue<string>();
        Require(role != null && string.Equals(role, user.Role, StringComparison.OrdinalIgnoreCase), "Tài khoản không còn quyền truy cập đơn dịch vụ.", 403, "FORBIDDEN");
        if (role is "Administrator" or "Admin" or "Dispatcher") return null;
        Require(role is "Transport Company" or "Carrier" or "Carrier Staff", "Tài khoản không có quyền truy cập đơn dịch vụ.", 403, "FORBIDDEN");
        var memberships = await Rows("SELECT carrier_id AS \"carrierId\" FROM carrier_users WHERE user_id=@p0" + (locked ? " FOR SHARE" : ""), ct, Actor);
        Require(memberships.Count == 1, "Tài khoản cần thuộc đúng một công ty vận tải.", 403, "SERVICE_DRAFT_MEMBERSHIP");
        return G(memberships[0], "carrierId");
    }

    private Task LockOrder(Guid id, CancellationToken ct)
        => Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))", ct, "eport-service-draft:" + id.ToString("N"));

    private const string OrderJoins = """
        FROM eport_service_drafts d
        JOIN eport_service_order_states o ON o.draft_id=d.id
        JOIN carriers c ON c.id=d.carrier_id
        LEFT JOIN eport_service_registrations r ON r.draft_id=d.id
        LEFT JOIN eport_services s ON s.id=d.main_service_id
        """;
    private const string OrderFields = """
        d.id,d.carrier_id AS "carrierId",COALESCE(r.number,d.number) AS number,d.number AS "draftNumber",
        r.id AS "registrationId",r.number AS "registrationNumber",d.flow,o.status,
        o.approval_status AS "approvalStatus",o.payment_status AS "paymentStatus",o.version,d.version AS "draftVersion",
        COALESCE(r.snapshot->>'mainServiceName',s.name,'Chưa chọn dịch vụ') AS "mainServiceName",
        COALESCE(r.snapshot->'sourceSnapshot'->>'bookingNumber',r.snapshot->'content'->'source'->>'referenceNumber',
          d.source_snapshot->>'bookingNumber',NULLIF(d.content->'source'->>'referenceNumber',''),'Chưa chọn hồ sơ') AS "sourceLabel",
        COALESCE(to_jsonb(c)->>'company_name',to_jsonb(c)->>'name','Doanh nghiệp') AS "companyName",
        jsonb_array_length(d.container_snapshots) AS "containerCount",r.total,r.currency,
        r.confirmed_at AS "confirmedAt",d.created_at AS "createdAt",o.updated_at AS "updatedAt",
        EXISTS(SELECT 1 FROM eport_service_order_events review WHERE review.draft_id=d.id AND review.action='PAYMENT_REVIEW_REQUIRED') AS "paymentReviewRequired",
        CASE WHEN NULLIF(r.snapshot->'content'->'delivery'->>'customsConfirmationScope','') IS NOT NULL THEN 'CUSTOMER_CONFIRMED'
          WHEN jsonb_array_length(COALESCE(NULLIF(r.snapshot->'operationalSnapshots','null'::jsonb),'[]'::jsonb))>0
          AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(COALESCE(NULLIF(r.snapshot->'operationalSnapshots','null'::jsonb),'[]'::jsonb)) x
            WHERE COALESCE(x->>'customsCleared','false')<>'true')
          THEN 'CLEARED_PORT_RECORD'
          WHEN jsonb_array_length(COALESCE(NULLIF(r.snapshot->'operationalSnapshots','null'::jsonb),'[]'::jsonb))>0
          AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(COALESCE(NULLIF(r.snapshot->'operationalSnapshots','null'::jsonb),'[]'::jsonb)) x
            WHERE COALESCE(x->>'customsCleared','false')<>'true' AND COALESCE(x->>'customsDemoCleared','false')<>'true')
          THEN 'DEMO_CLEARED' ELSE 'NOT_VERIFIED' END AS "customsStatus"
        """;

    private async Task<JsonObject> OrderRow(Guid id, Guid? company, CancellationToken ct, bool locked = false)
    {
        var rows = await Rows("SELECT " + OrderFields + """
            ,COALESCE(r.snapshot->'content',d.content) AS content,
             COALESCE(r.snapshot->'sourceSnapshot',d.source_snapshot) AS "sourceSnapshot",
             COALESCE(r.snapshot->'containers',d.container_snapshots) AS containers,
             COALESCE(r.snapshot->'fees','[]'::jsonb) AS fees
            """ + "\n" + OrderJoins + " WHERE d.id=@p0 AND (@p1 OR d.carrier_id=@p2) AND (NOT @p1 OR r.id IS NOT NULL)" +
            (locked ? " FOR UPDATE OF o" : ""), ct, id, !company.HasValue, company ?? Guid.Empty);
        Require(rows.Count == 1, "Không tìm thấy đơn dịch vụ.", 404, "SERVICE_ORDER_NOT_FOUND");
        return rows[0];
    }

    private static string[] AllowedActions(JsonObject row, Guid? company)
    {
        var result = new List<string>();
        var status = S(row, "status");
        var payment = S(row, "paymentStatus");
        var reviewRequired = row["paymentReviewRequired"]?.GetValue<bool>() == true;
        var unsettled = payment is not ("PENDING" or "PAID") && !reviewRequired;
        if (company.HasValue && status == "DRAFT") result.AddRange(["EDIT", "REVIEW"]);
        if (status is "DRAFT" or "CONFIRMED" or "PENDING_APPROVAL" && unsettled) result.Add("CANCEL");
        if (!company.HasValue)
        {
            if (status is "CONFIRMED" or "PENDING_APPROVAL") result.Add("RECEIVE");
            if (status == "RECEIVED" && S(row, "approvalStatus") == "PENDING") result.Add("APPROVE");
            if (status == "RECEIVED" && unsettled) result.Add("REJECT");
        }
        if (!reviewRequired && company.HasValue && row["registrationId"] != null &&
            status is "CONFIRMED" or "RECEIVED" or "APPROVED" &&
            S(row, "approvalStatus") is "NOT_REQUIRED" or "APPROVED" &&
            payment is "UNPAID" or "FAILED" && (row["total"]?.GetValue<decimal>() ?? 0) > 0)
            result.Add("CREATE_PAYMENT");
        return result.ToArray();
    }

    private static JsonObject OrderView(JsonObject row, Guid? company)
    {
        var result = row.DeepClone().AsObject();
        result["staff"] = !company.HasValue;
        result["allowedActions"] = new JsonArray(AllowedActions(row, company).Select(action => (JsonNode?)JsonValue.Create(action)).ToArray());
        return result;
    }

    public async Task<object> OrdersList(string? search, string? status, string? paymentStatus, string? flow, int page, int pageSize, CancellationToken ct)
    {
        var company = await OrderAccess(ct);
        var term = ServiceDraftRules.Search(search);
        status = (status ?? "").Trim(); paymentStatus = (paymentStatus ?? "").Trim(); flow = (flow ?? "").Trim();
        Require(status is "" or "DRAFT" or "CONFIRMED" or "PENDING_APPROVAL" or "RECEIVED" or "APPROVED" or "REJECTED" or "CANCELLED", "Trạng thái đơn không hợp lệ.");
        Require(paymentStatus is "" or "UNPAID" or "NOT_REQUIRED" or "PENDING" or "PAID" or "FAILED", "Trạng thái thanh toán không hợp lệ.");
        Require(flow is "" or "EXPORT" or "IMPORT", "Luồng hàng không hợp lệ.");
        Require(page is >= 1 and <= 100000 && pageSize is >= 1 and <= 50, "Phân trang không hợp lệ, tối đa 50 đơn mỗi trang.");
        var filter = OrderJoins + "\n" + """
            WHERE (@p0 OR d.carrier_id=@p1) AND (NOT @p0 OR r.id IS NOT NULL)
             AND (@p3='' OR o.status=@p3) AND (@p4='' OR o.payment_status=@p4) AND (@p5='' OR d.flow=@p5)
             AND (d.number ILIKE @p2 OR COALESCE(r.number,'') ILIKE @p2
              OR COALESCE(r.snapshot->>'mainServiceName',s.name,'') ILIKE @p2
              OR COALESCE(r.snapshot->'sourceSnapshot'->>'bookingNumber',d.source_snapshot->>'bookingNumber','') ILIKE @p2
              OR COALESCE(d.content->'source'->>'referenceNumber','') ILIKE @p2
              OR EXISTS(SELECT 1 FROM jsonb_array_elements(d.container_snapshots) k WHERE k->>'containerNumber' ILIKE @p2)
              OR (@p0 AND COALESCE(to_jsonb(c)->>'company_name',to_jsonb(c)->>'name','') ILIKE @p2))
            """;
        object?[] args = [!company.HasValue, company ?? Guid.Empty, "%" + term + "%", status, paymentStatus, flow];
        var count = (await Rows("SELECT count(*) AS total " + filter, ct, args))[0]["total"]!.GetValue<long>();
        var rows = await Rows("SELECT " + OrderFields + "\n" + filter + " ORDER BY o.updated_at DESC,d.id LIMIT @p6 OFFSET @p7", ct,
            [.. args, pageSize, (page - 1) * pageSize]);
        return new { items = rows.Select(row => OrderView(row, company)), total = count, page, pageSize, staff = !company.HasValue };
    }

    public async Task<JsonObject> GetOrder(Guid id, CancellationToken ct)
    {
        var company = await OrderAccess(ct);
        return OrderView(await OrderRow(id, company, ct), company);
    }

    public async Task<object> OrderHistory(Guid id, CancellationToken ct)
    {
        var company = await OrderAccess(ct);
        await OrderRow(id, company, ct);
        var rows = await Rows("""
            SELECT e.id,e.action,e.from_status AS "fromStatus",e.to_status AS "toStatus",e.reason,e.data,
             COALESCE(NULLIF(to_jsonb(u)->>'full_name',''),NULLIF(to_jsonb(u)->>'username',''),'Hệ thống') AS "actorName",
             e.created_at AS "createdAt"
            FROM eport_service_order_events e LEFT JOIN users u ON u.id=e.actor_id
            WHERE e.draft_id=@p0 ORDER BY e.created_at,e.id
            """, ct, id);
        return new { items = rows };
    }

    private Task<int> AddEvent(Guid id, string action, string? fromStatus, string? toStatus, string reason, CancellationToken ct,
        Guid? requestId = null, string? fingerprint = null, JsonObject? data = null)
        => Exec("""
            INSERT INTO eport_service_order_events(id,draft_id,actor_id,action,from_status,to_status,reason,request_id,fingerprint,data)
             VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,CAST(@p9 AS jsonb))
            """, ct, Guid.NewGuid(), id, Actor, action, fromStatus, toStatus, reason, requestId, fingerprint, (data ?? new JsonObject()).ToJsonString());

    public async Task<JsonObject> OrderAction(Guid id, ServiceOrderActionInput input, CancellationToken ct)
    {
        Require(input.RequestId != Guid.Empty && input.ExpectedVersion > 0, "Bổ sung mã yêu cầu và phiên bản đơn dịch vụ.");
        var action = (input.Action ?? "").Trim().ToUpperInvariant();
        var reason = (input.Reason ?? "").Trim();
        Require(action is "CANCEL" or "RECEIVE" or "APPROVE" or "REJECT", "Thao tác đơn dịch vụ không hợp lệ.");
        Require(reason.Length <= 1000 && !reason.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')), "Lý do tối đa 1.000 ký tự và không chứa ký tự điều khiển.");
        Require(action is not ("CANCEL" or "REJECT") || reason.Length >= 3, "Nhập lý do hủy hoặc từ chối (ít nhất 3 ký tự).");
        var evidence = (input.SourceEvidenceReference ?? "").Trim();
        Require(evidence.Length <= 200 && !evidence.Any(char.IsControl), "Tham chiếu chứng từ tối đa 200 ký tự.");
        var fingerprintPayload = input.SourceDocumentsChecked || evidence != ""
            ? JsonSerializer.Serialize(new { action, reason, actor = Actor, input.SourceDocumentsChecked, evidence })
            : JsonSerializer.Serialize(new { action, reason, actor = Actor });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintPayload)));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var company = await OrderAccess(ct);
        if (action != "CANCEL") Require(!company.HasValue, "Chỉ nhân viên cảng được tiếp nhận và duyệt đơn.", 403, "FORBIDDEN");
        await LockOrder(id, ct);
        var row = await OrderRow(id, company, ct, true);
        var replay = await Rows("SELECT fingerprint,actor_id AS \"actorId\" FROM eport_service_order_events WHERE draft_id=@p0 AND request_id=@p1", ct, id, input.RequestId);
        if (replay.Count > 0)
        {
            Require(S(replay[0], "fingerprint") == fingerprint && G(replay[0], "actorId") == Actor, "Mã yêu cầu đã được dùng cho thao tác khác.", 409, "SERVICE_REQUEST_REUSED");
            await tx.CommitAsync(ct);
            return OrderView(row, company);
        }
        Require(row["version"]!.GetValue<int>() == input.ExpectedVersion, "Đơn vừa thay đổi. Tải lại và kiểm tra trạng thái trước khi thao tác.", 409, "SERVICE_ORDER_VERSION_CONFLICT");
        Require(AllowedActions(row, company).Contains(action), "Trạng thái hiện tại không cho phép thao tác này. Đơn đã tiếp nhận hoặc đang thanh toán cần được cảng xử lý.", 409, "SERVICE_ORDER_STATE_CONFLICT");
        var active = await Rows("SELECT id FROM eport_service_payments WHERE draft_id=@p0 AND status IN ('PENDING','SUBMITTED','SUCCEEDED')", ct, id);
        Require(action is not ("CANCEL" or "REJECT") || active.Count == 0, "Đơn có giao dịch đang xử lý hoặc đã thanh toán; cần đối soát trước khi hủy hoặc từ chối.", 409, "SERVICE_ORDER_PAYMENT_LOCKED");
        var next = action switch { "CANCEL" => "CANCELLED", "RECEIVE" => "RECEIVED", "APPROVE" => "APPROVED", _ => "REJECTED" };
        var documentReview = action == "APPROVE" && S(row, "flow") == "IMPORT" && row["content"]?["workflowVersion"]?.GetValue<int>() == 2;
        Require(!documentReview || input.SourceDocumentsChecked && evidence.Length >= 3,
            "Duyệt hàng nhập cần xác nhận đã đối chiếu DO/eDO, container, hiệu lực và quyền nhận hàng; ghi tham chiếu chứng từ làm căn cứ.", 422, "SERVICE_SOURCE_REVIEW_REQUIRED");
        var approval = action == "APPROVE" ? "APPROVED" : action == "REJECT" ? "REJECTED" : S(row, "approvalStatus");
        await Exec("UPDATE eport_service_order_states SET status=@p1,approval_status=@p2,version=version+1,updated_at=now() WHERE draft_id=@p0", ct, id, next, approval);
        await AddEvent(id, action, S(row, "status"), next, reason, ct, input.RequestId, fingerprint,
            documentReview ? new JsonObject { ["sourceDocumentsChecked"] = true, ["sourceEvidenceReference"] = evidence,
                ["verificationMethod"] = "PORT_MANUAL_DOCUMENT_REVIEW", ["customsClearanceGranted"] = false } : null);
        var saved = OrderView(await OrderRow(id, company, ct), company);
        await tx.CommitAsync(ct);
        return saved;
    }
}

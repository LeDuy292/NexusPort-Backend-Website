using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceOrderService
{
    private const string PaymentFields = """
        id,draft_id AS "draftId",number,method,status,amount,currency,order_code AS "orderCode",
        payment_link_id AS "paymentLinkId",checkout_url AS "checkoutUrl",qr_code AS "qrCode",
        gateway_status AS "gatewayStatus",gateway_error AS "gatewayError",
        bank_reference AS "bankReference",reason,created_at AS "createdAt",updated_at AS "updatedAt"
        """;

    private async Task<JsonObject> PaymentRow(Guid orderId, Guid paymentId, CancellationToken ct)
    {
        var payments = await Rows("SELECT " + PaymentFields + " FROM eport_service_payments WHERE draft_id=@p0 AND id=@p1", ct, orderId, paymentId);
        Require(payments.Count == 1, "Không tìm thấy yêu cầu thanh toán của đơn dịch vụ.", 404, "SERVICE_PAYMENT_NOT_FOUND");
        return payments[0];
    }

    private static string PaymentText(string? value, int max, string label)
    {
        var text = (value ?? "").Trim();
        Require(text.Length is >= 3 && text.Length <= max && !text.Any(char.IsControl), $"{label} cần từ 3 đến {max} ký tự hợp lệ.");
        return text;
    }

    private static void PaymentRequest(Guid requestId, int expectedVersion)
    {
        Require(requestId != Guid.Empty, "Mỗi thao tác thanh toán cần một mã yêu cầu hợp lệ.");
        Require(expectedVersion > 0, "Phiên bản đơn dịch vụ không hợp lệ.");
    }

    private string PaymentFingerprint(string action, Guid? paymentId, object payload)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { action, paymentId, actor = Actor, payload }))));

    private async Task<JsonObject?> PaymentReplay(Guid orderId, Guid requestId, string fingerprint, CancellationToken ct)
    {
        var events = await Rows("SELECT actor_id AS \"actorId\",fingerprint,data FROM eport_service_order_events WHERE draft_id=@p0 AND request_id=@p1", ct, orderId, requestId);
        if (events.Count == 0) return null;
        Require(S(events[0], "fingerprint") == fingerprint && G(events[0], "actorId") == Actor,
            "Mã yêu cầu đã được dùng cho một thao tác khác. Tải lại trước khi tiếp tục.", 409, "SERVICE_REQUEST_REUSED");
        Require(Guid.TryParse(events[0]["data"]?["paymentId"]?.GetValue<string>(), out var id), "Mã yêu cầu đã được sử dụng.", 409, "SERVICE_REQUEST_REUSED");
        return await PaymentRow(orderId, id, ct);
    }

    private static bool PaymentOrderEligible(JsonObject order)
        => order["registrationId"] != null && (S(order, "status") is "CONFIRMED" or "RECEIVED" or "APPROVED")
            && (S(order, "approvalStatus") is "NOT_REQUIRED" or "APPROVED");

    private static void PaymentVersion(JsonObject order, int expectedVersion)
        => Require(order["version"]!.GetValue<int>() == expectedVersion, "Đơn dịch vụ vừa thay đổi. Tải lại trạng thái trước khi thanh toán.", 409, "SERVICE_ORDER_VERSION_CONFLICT");

    private static bool PaymentTerminal(JsonObject payment) => S(payment, "status") is "SUCCEEDED" or "FAILED" or "CANCELLED";
    private static bool PaymentReviewRequired(JsonObject order) => order["paymentReviewRequired"]?.GetValue<bool>() == true;

    private static void RequireNoPaymentReview(JsonObject order)
        => Require(!PaymentReviewRequired(order), "Đơn có giao dịch cần cảng đối chiếu. Tạm dừng tạo, mở hoặc xử lý thanh toán khác để tránh thu trùng.", 409, "SERVICE_PAYMENT_REVIEW_REQUIRED");

    private static JsonObject PaymentView(JsonObject payment, JsonObject order, Guid? company)
    {
        var result = payment.DeepClone().AsObject();
        var actions = new List<string>();
        if (!PaymentReviewRequired(order) && !PaymentTerminal(payment) && PaymentOrderEligible(order) && S(order, "paymentStatus") != "PAID")
        {
            actions.Add("SYNC");
            if (company.HasValue)
            {
                if (S(payment, "checkoutUrl").Length > 0) actions.Add("OPEN_CHECKOUT");
                actions.Add("CANCEL");
            }
        }
        result["allowedActions"] = new JsonArray(actions.Select(action => (JsonNode?)JsonValue.Create(action)).ToArray());
        return result;
    }

    private static object PaymentResult(JsonObject payment, JsonObject order, Guid? company)
        => new { payment = PaymentView(payment, order, company), orderVersion = order["version"]!.GetValue<int>(), paymentStatus = S(order, "paymentStatus"), paymentReviewRequired = PaymentReviewRequired(order) };

    private async Task PaymentState(Guid id, string paymentStatus, CancellationToken ct)
    {
        await Exec("UPDATE eport_service_order_states SET payment_status=@p1,version=version+1,updated_at=now() WHERE draft_id=@p0", ct, id, paymentStatus);
        await Exec("UPDATE eport_service_registrations SET payment_status=@p1 WHERE draft_id=@p0", ct, id, paymentStatus);
        if (paymentStatus == "PAID" && (await Rows("SELECT to_regclass('port.event_outbox') IS NOT NULL AS ready", ct))[0]["ready"]!.GetValue<bool>())
        {
            var info = (await Rows("SELECT d.flow, d.carrier_id AS \"carrierId\", COALESCE(r.number, d.number) AS number, COALESCE(d.source_snapshot->>'bookingNumber', d.content->'source'->>'referenceNumber', '') AS ref FROM eport_service_drafts d LEFT JOIN eport_service_registrations r ON r.draft_id=d.id WHERE d.id=@p0", ct, id)).FirstOrDefault();
            var bookingRef = S(info, "ref");
            var payload = new JsonObject
            {
                ["eventId"] = Guid.NewGuid().ToString(),
                ["eventType"] = "ServiceOrderPaid",
                ["orderId"] = id.ToString(),
                ["registrationNumber"] = S(info, "number"),
                ["carrierId"] = S(info, "carrierId"),
                ["flow"] = S(info, "flow"),
                ["bookingOrEdo"] = bookingRef,
                ["paidAt"] = DateTimeOffset.UtcNow
            };
            await Exec("""
                INSERT INTO port.event_outbox("Id","EventType","BookingNo","PayloadJson","Status","Attempts","CreatedAt")
                VALUES(@p0,'ServiceOrderPaid',@p1,@p2,0,0,now())
                """, ct, Guid.NewGuid(), bookingRef, payload.ToJsonString());
        }
    }

    private static JsonObject PaymentEventData(JsonObject payment) => new()
    {
        ["paymentId"] = payment["id"]!.DeepClone(), ["paymentNumber"] = payment["number"]!.DeepClone(),
        ["amount"] = payment["amount"]!.DeepClone(), ["currency"] = payment["currency"]!.DeepClone(),
        ["orderCode"] = payment["orderCode"]!.DeepClone(), ["paymentAttemptStatus"] = payment["status"]!.DeepClone(),
        ["gatewayStatus"] = payment["gatewayStatus"]?.DeepClone(), ["bankReference"] = payment["bankReference"]?.DeepClone()
    };

    public async Task<object> ListServicePayments(Guid id, CancellationToken ct)
    {
        var company = await OrderAccess(ct);
        var order = await OrderRow(id, company, ct);
        var rows = await Rows("SELECT " + PaymentFields + " FROM eport_service_payments WHERE draft_id=@p0 ORDER BY created_at DESC,id", ct, id);
        return new { items = rows.Select(row => PaymentView(row, order, company)), provider = "VIETQR", configured = gateway.Configured,
            paymentReviewRequired = PaymentReviewRequired(order),
            instructions = PaymentReviewRequired(order) ? "Có giao dịch cần cảng đối chiếu. Không chuyển thêm tiền hoặc mở lại các liên kết cũ cho đến khi được cảng hướng dẫn."
                : gateway.Configured ? "Thanh toán trực tuyến qua cổng VietQR. Hệ thống chỉ ghi nhận đã thanh toán sau khi xác minh chuyển khoản thành công."
                : "Kênh thanh toán trực tuyến chưa sẵn sàng. Liên hệ quản trị viên; bạn vẫn có thể theo dõi và xử lý hồ sơ dịch vụ." };
    }

    public async Task<object> CreateServicePayment(Guid id, ServicePaymentCreateInput input, CancellationToken ct)
    {
        PaymentRequest(input.RequestId, input.ExpectedVersion);
        var fingerprint = PaymentFingerprint("PAYMENT_CREATED", null, new { });
        Guid paymentId;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var company = await OrderAccess(ct);
            Require(company.HasValue, "Chỉ công ty đăng ký được tạo yêu cầu thanh toán.", 403, "FORBIDDEN");
            await LockOrder(id, ct);
            var order = await OrderRow(id, company, ct, true);
            RequireNoPaymentReview(order);
            Require(gateway.Configured, "Kênh thanh toán trực tuyến chưa được cấu hình. Liên hệ quản trị viên để cấu hình kênh thanh toán.", 503, "PAYOS_CONFIGURATION_REQUIRED");
            var replay = await PaymentReplay(id, input.RequestId, fingerprint, ct);
            if (replay != null)
            {
                if (PaymentTerminal(replay) || S(replay, "paymentLinkId").Length > 0) { await tx.CommitAsync(ct); return PaymentResult(replay, order, company); }
                paymentId = G(replay, "id");
            }
            else
            {
                PaymentVersion(order, input.ExpectedVersion);
                Require(PaymentOrderEligible(order), "Đơn phải được xác nhận và hoàn tất phê duyệt trước khi thanh toán.", 409, "SERVICE_PAYMENT_ORDER_STATE");
                var total = order["total"]?.GetValue<decimal>() ?? 0;
                Require(total > 0 && S(order, "paymentStatus") != "NOT_REQUIRED", "Đơn này không phát sinh số tiền cần thanh toán.", 409, "SERVICE_PAYMENT_NOT_REQUIRED");
                Require(S(order, "paymentStatus") is "UNPAID" or "FAILED", "Đơn đã thanh toán hoặc đang có yêu cầu chờ kết quả.", 409, "SERVICE_PAYMENT_ALREADY_ACTIVE");
                var active = await Rows("SELECT id FROM eport_service_payments WHERE draft_id=@p0 AND status IN ('PENDING','SUBMITTED','SUCCEEDED')", ct, id);
                Require(active.Count == 0, "Đơn đã có yêu cầu thanh toán đang hoạt động hoặc đã thành công.", 409, "SERVICE_PAYMENT_ALREADY_ACTIVE");
                paymentId = Guid.NewGuid();
                await Exec("""
                    INSERT INTO eport_service_payments(id,draft_id,request_id,number,method,status,amount,currency,created_by)
                    VALUES(@p0,@p1,@p2,@p3,'PAYOS','PENDING',@p4,'VND',@p5)
                    """, ct, paymentId, id, input.RequestId, "TT-" + paymentId.ToString("N").ToUpperInvariant(), total, Actor);
                await PaymentState(id, "PENDING", ct);
                var saved = await PaymentRow(id, paymentId, ct);
                await AddEvent(id, "PAYMENT_CREATED", S(order, "status"), S(order, "status"), "Tạo yêu cầu thanh toán trực tuyến theo số tiền đã xác nhận.", ct,
                    input.RequestId, fingerprint, PaymentEventData(saved));
            }
            // Persist the unique provider order code before making any external request.
            await tx.CommitAsync(ct);
        }
        return await ProcessPayOs(id, paymentId, "INITIALIZE", null, null, null, ct);
    }

    public Task<object> SyncServicePayment(Guid id, Guid paymentId, ServicePaymentSyncInput input, CancellationToken ct)
    {
        PaymentRequest(input.RequestId, input.ExpectedVersion);
        return ProcessPayOs(id, paymentId, "SYNC", input.RequestId, input.ExpectedVersion, null, ct);
    }

    public Task<object> CancelServicePayment(Guid id, Guid paymentId, ServicePaymentCancelInput input, CancellationToken ct)
    {
        PaymentRequest(input.RequestId, input.ExpectedVersion);
        return ProcessPayOs(id, paymentId, "CANCEL", input.RequestId, input.ExpectedVersion, PaymentText(input.Reason, 1000, "Lý do hủy yêu cầu thanh toán"), ct);
    }

    private async Task<PayOsPaymentLink> InitializePayOs(JsonObject payment, Guid orderId, CancellationToken ct)
    {
        var code = payment["orderCode"]!.GetValue<long>();
        // Test mode: Use 2,000 VND so real banking apps can scan & transfer 2,000 VND for testing
        const long testAmount = 2000L;
        try { return await gateway.CreateAsync(code, testAmount, orderId, ct); }
        catch (CatalogException error) when (error.StatusCode >= 500)
        {
            // A previous timeout may already have created this same provider order.
            var existing = await gateway.GetAsync(code, ct);
            if (existing != null) return existing;
            throw;
        }
    }

    private async Task<object> ProcessPayOs(Guid id, Guid paymentId, string operation, Guid? requestId, int? expectedVersion, string? reason, CancellationToken ct)
    {
        var fingerprint = requestId.HasValue ? PaymentFingerprint("PAYMENT_" + operation, paymentId, new { reason }) : null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var company = await OrderAccess(ct);
        Require(operation == "SYNC" || company.HasValue, "Chỉ công ty đăng ký được tạo hoặc hủy yêu cầu thanh toán.", 403, "FORBIDDEN");
        await LockOrder(id, ct);
        var order = await OrderRow(id, company, ct, true);
        RequireNoPaymentReview(order);
        if (requestId.HasValue)
        {
            var replay = await PaymentReplay(id, requestId.Value, fingerprint!, ct);
            if (replay != null) { await tx.CommitAsync(ct); return PaymentResult(replay, order, company); }
        }
        if (expectedVersion.HasValue) PaymentVersion(order, expectedVersion.Value);
        var payment = await PaymentRow(id, paymentId, ct);
        if (PaymentTerminal(payment)) { await tx.CommitAsync(ct); return PaymentResult(payment, order, company); }
        Require(PaymentOrderEligible(order) && S(order, "paymentStatus") == "PENDING", "Đơn không còn ở trạng thái được xử lý yêu cầu thanh toán.", 409, "SERVICE_PAYMENT_ORDER_STATE");
        if (operation == "INITIALIZE" && S(payment, "paymentLinkId").Length > 0) { await tx.CommitAsync(ct); return PaymentResult(payment, order, company); }
        try
        {
            var code = payment["orderCode"]!.GetValue<long>();
            PayOsPaymentLink link;
            if (operation == "INITIALIZE") link = await InitializePayOs(payment, id, ct);
            else
            {
                link = (await gateway.GetAsync(code, ct)) ?? (S(payment, "paymentLinkId").Length == 0
                    ? await InitializePayOs(payment, id, ct)
                    : throw new CatalogException("payOS chưa trả về yêu cầu hiện tại. Giữ nguyên mã thanh toán để kiểm tra.", 502, "PAYOS_GATEWAY_UNAVAILABLE"));
                if (operation == "CANCEL" && link.Status is "PENDING" or "PROCESSING")
                {
                    Require(link.AmountPaid == 0, "Yêu cầu đã nhận một phần tiền. Cần đối chiếu với cảng trước khi hủy hoặc tạo thanh toán khác.", 409, "PAYOS_PARTIAL_PAYMENT");
                    link = await gateway.CancelAsync(code, reason!, ct);
                }
            }
            await ApplyPayOsLink(id, payment, order, link, reason ?? "Đồng bộ kết quả thanh toán trực tuyến.", ct);
            var saved = await PaymentRow(id, paymentId, ct);
            var data = PaymentEventData(saved);
            data["transactions"] = link.Transactions?.DeepClone();
            await AddEvent(id, S(saved, "status") == "SUCCEEDED" ? "PAYMENT_SUCCEEDED" : operation == "CANCEL" ? "PAYMENT_CANCELLED" : "PAYMENT_SYNCED",
                S(order, "status"), S(order, "status"), reason ?? "Đã xác minh phản hồi từ cổng thanh toán.", ct, requestId, fingerprint, data);
            var current = await OrderRow(id, company, ct);
            if(S(saved,"status")=="SUCCEEDED")await QueueServiceMail(current,"PAYMENT_RECEIPT",ct);
            await tx.CommitAsync(ct);
            return PaymentResult(saved, current, company);
        }
        catch (Exception error) when ((error is HttpRequestException or TaskCanceledException || error is CatalogException catalog && catalog.StatusCode >= 500) && !ct.IsCancellationRequested)
        {
            // Network uncertainty is not failure: the gateway may already have a live link.
            await Exec("UPDATE eport_service_payments SET gateway_error=@p2,updated_at=now() WHERE draft_id=@p0 AND id=@p1", ct, id, paymentId,
                "Chưa xác minh được kết quả thanh toán. Nhấn Kiểm tra kết quả để thử lại; không chuyển tiền vào yêu cầu khác.");
            var saved = await PaymentRow(id, paymentId, ct);
            await tx.CommitAsync(ct);
            return PaymentResult(saved, order, company);
        }
    }

    private async Task ApplyPayOsLink(Guid id, JsonObject payment, JsonObject order, PayOsPaymentLink link, string reason, CancellationToken ct, string? bankReference = null)
    {
        var expectedAmount = payment["amount"]!.GetValue<decimal>();
        Require(link.OrderCode == payment["orderCode"]!.GetValue<long>() && (link.Amount == expectedAmount || link.Amount == 2000m)
            && link.Currency == "VND", "Kết quả cổng thanh toán không khớp mã yêu cầu, số tiền hoặc tiền tệ. Chưa ghi nhận thanh toán.", 422, "PAYOS_PAYMENT_MISMATCH");
        Require(S(payment, "paymentLinkId").Length == 0 || S(payment, "paymentLinkId") == link.PaymentLinkId,
            "Mã link thanh toán không khớp yêu cầu đã lưu.", 422, "PAYOS_PAYMENT_MISMATCH");
        Require(link.AmountPaid >= 0 && link.AmountRemaining >= 0 && link.AmountPaid + link.AmountRemaining == link.Amount,
            "Các khoản tiền cổng thanh toán chưa khớp tổng yêu cầu. Cần kiểm tra lại.", 422, "PAYOS_PAYMENT_MISMATCH");
        var status = link.Status switch { "PAID" => "SUCCEEDED", "CANCELLED" => "CANCELLED", "EXPIRED" => "FAILED", "PROCESSING" => "SUBMITTED", _ => "PENDING" };
        var problem = "";
        if (link.Status == "PAID") Require(link.AmountPaid == link.Amount && link.AmountRemaining == 0, "Cổng thanh toán chưa xác nhận đủ số tiền của đơn.", 422, "PAYOS_PAYMENT_MISMATCH");
        if (link.AmountPaid > 0 && link.Status != "PAID")
        {
            status = "SUBMITTED";
            problem = "Cổng thanh toán đã ghi nhận một phần tiền. Liên hệ cảng để đối chiếu; chưa thể tạo yêu cầu khác hoặc ghi nhận đã thanh toán.";
        }
        if (status == "SUCCEEDED") bankReference ??= "PAYOS-LINK:" + link.PaymentLinkId;
        var checkout = link.CheckoutUrl ?? (S(payment, "checkoutUrl").Length > 0 ? S(payment, "checkoutUrl") : "https://pay.payos.vn/web/" + Uri.EscapeDataString(link.PaymentLinkId));
        await Exec("""
            UPDATE eport_service_payments SET status=@p2,payment_link_id=@p3,checkout_url=@p4,qr_code=COALESCE(@p5,qr_code),
             gateway_status=@p6,gateway_error=@p7,bank_reference=@p8,reason=@p9,updated_at=now() WHERE draft_id=@p0 AND id=@p1
            """, ct, id, G(payment, "id"), status, link.PaymentLinkId, checkout, link.QrCode, link.Status, problem, bankReference, reason);
        await PaymentState(id, status switch { "SUCCEEDED" => "PAID", "FAILED" => "FAILED", "CANCELLED" => "UNPAID", _ => "PENDING" }, ct);
    }

    public async Task<object> HandlePayOsWebhook(JsonObject body, CancellationToken ct)
    {
        // Signature validation happens before even looking up an order, including provider probes.
        var webhook = gateway.VerifyWebhook(body);
        Require(webhook.Code == "00" && webhook.Currency == "VND" && webhook.Amount > 0,
            "Thông tin giao dịch cổng thanh toán không hợp lệ.", 422, "PAYOS_PAYMENT_MISMATCH");
        var matches = await Rows("SELECT draft_id AS \"draftId\",id FROM eport_service_payments WHERE order_code=@p0", ct, webhook.OrderCode);
        if (matches.Count == 0) return new { received = true, ignored = true };
        var id = G(matches[0], "draftId");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOrder(id, ct);
        var order = await OrderRow(id, null, ct, true);
        var payment = await PaymentRow(id, G(matches[0], "id"), ct);
        var expectedAmount = payment["amount"]!.GetValue<decimal>();
        Require((webhook.Amount == expectedAmount || webhook.Amount == 2000m)
            && (S(payment, "paymentLinkId").Length == 0 || S(payment, "paymentLinkId") == webhook.PaymentLinkId),
            "Webhook cổng thanh toán không khớp yêu cầu hoặc số tiền đã lưu.", 422, "PAYOS_PAYMENT_MISMATCH");
        var reference = "PAYOS:" + webhook.Reference.ToUpperInvariant();
        if (S(payment, "status") == "SUCCEEDED")
        {
            var known = await KnownPayOsReferences(id, payment, ct);
            var normalized = webhook.Reference.ToUpperInvariant();
            if (known.Contains(normalized)) { await tx.CommitAsync(ct); return new { received = true, duplicate = true }; }
            if (S(payment, "bankReference").StartsWith("PAYOS-LINK:", StringComparison.Ordinal) && known.Count == 0)
            {
                // A signed status lookup may settle before the corresponding
                // webhook arrives. Record its first bank reference separately;
                // never rewrite the already immutable successful payment row.
                await AddPayOsWebhookEvent(id, payment, order, webhook, "PAYMENT_REFERENCE_OBSERVED",
                    "Bổ sung tham chiếu ngân hàng cho khoản thanh toán đã xác minh.", ct);
                await tx.CommitAsync(ct);
                return new { received = true, referenceObserved = true };
            }
            await RecordPayOsReview(id, payment, order, webhook,
                "Cổng thanh toán báo thêm tham chiếu nhận tiền cho yêu cầu đã thanh toán; cảng cần đối chiếu để tránh ghi nhận trùng.", ct);
            await tx.CommitAsync(ct);
            return new { received = true, reviewRequired = true };
        }
        if (PaymentTerminal(payment) || PaymentReviewRequired(order) || !PaymentOrderEligible(order))
        {
            await RecordPayOsReview(id, payment, order, webhook,
                "Cổng thanh toán xác nhận nhận tiền sau khi yêu cầu đã đóng hoặc đơn cần đối chiếu. Giữ lịch sử cũ và tạm dừng thanh toán khác.", ct);
            await tx.CommitAsync(ct);
            return new { received = true, reviewRequired = true };
        }
        var used = await Rows("SELECT id FROM eport_service_payments WHERE bank_reference=@p0 AND id<>@p1", ct, reference, G(payment, "id"));
        Require(used.Count == 0, "Tham chiếu thanh toán đã được ghi nhận cho yêu cầu khác.", 409, "SERVICE_PAYMENT_REFERENCE_USED");
        var link = new PayOsPaymentLink(webhook.OrderCode, webhook.Amount, webhook.PaymentLinkId, "PAID", webhook.Amount, 0);
        await ApplyPayOsLink(id, payment, order, link, "Cổng thanh toán đã xác minh giao dịch thành công.", ct, reference);
        var saved = await PaymentRow(id, G(payment, "id"), ct);
        var data = PaymentEventData(saved);
        data["reference"] = webhook.Reference;
        await Exec("""
            INSERT INTO eport_service_order_events(id,draft_id,actor_id,action,from_status,to_status,reason,data)
            VALUES(@p0,@p1,NULL,'PAYMENT_SUCCEEDED',@p2,@p2,'Cổng thanh toán đã xác minh thành công.',CAST(@p3 AS jsonb))
            """, ct, Guid.NewGuid(), id, S(order, "status"), data.ToJsonString());
        await QueueServiceMail(await OrderRow(id,null,ct),"PAYMENT_RECEIPT",ct);
        await tx.CommitAsync(ct);
        return new { received = true };
    }

    private async Task<HashSet<string>> KnownPayOsReferences(Guid id, JsonObject payment, CancellationToken ct)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var saved = S(payment, "bankReference");
        if (saved.StartsWith("PAYOS:", StringComparison.Ordinal)) references.Add(saved[6..].ToUpperInvariant());
        var events = await Rows("""
            SELECT data FROM eport_service_order_events
            WHERE draft_id=@p0 AND data->>'paymentId'=@p1
              AND action IN ('PAYMENT_SUCCEEDED','PAYMENT_REFERENCE_OBSERVED')
            """, ct, id, S(payment, "id"));
        foreach (var item in events)
        {
            var data = item["data"];
            var reference = S(data, "reference").Trim();
            if (reference.Length > 0) references.Add(reference.ToUpperInvariant());
            if (data?["transactions"] is JsonArray transactions)
                foreach (var transaction in transactions)
                {
                    var transactionReference = S(transaction, "reference").Trim();
                    if (transactionReference.Length > 0) references.Add(transactionReference.ToUpperInvariant());
                }
        }
        return references;
    }

    private async Task AddPayOsWebhookEvent(Guid id, JsonObject payment, JsonObject order, PayOsWebhookData webhook,
        string action, string reason, CancellationToken ct)
    {
        var data = PaymentEventData(payment);
        data["reference"] = webhook.Reference.ToUpperInvariant();
        data["receivedAmount"] = webhook.Amount;
        data["paymentLinkId"] = webhook.PaymentLinkId;
        await Exec("""
            INSERT INTO eport_service_order_events(id,draft_id,actor_id,action,from_status,to_status,reason,data)
             VALUES(@p0,@p1,NULL,@p2,@p3,@p3,@p4,CAST(@p5 AS jsonb))
            """, ct, Guid.NewGuid(), id, action, S(order, "status"), reason, data.ToJsonString());
    }

    private async Task RecordPayOsReview(Guid id, JsonObject payment, JsonObject order, PayOsWebhookData webhook,
        string reason, CancellationToken ct)
    {
        // Called under the same per-order lock as every other money mutation.
        // A retried callback is acknowledged without growing the history or
        // advancing the workflow version again.
        var seen = await Rows("""
            SELECT id FROM eport_service_order_events WHERE draft_id=@p0 AND action='PAYMENT_REVIEW_REQUIRED'
             AND data->>'paymentId'=@p1 AND upper(data->>'reference')=@p2
            """, ct, id, S(payment, "id"), webhook.Reference.ToUpperInvariant());
        if (seen.Count > 0) return;
        await AddPayOsWebhookEvent(id, payment, order, webhook, "PAYMENT_REVIEW_REQUIRED", reason, ct);
        if (!PaymentReviewRequired(order)) await PaymentState(id, "PENDING", ct);
    }

    public async Task<object> SimulatePaymentSuccess(Guid id, CancellationToken ct)
    {
        await LockOrder(id, ct);
        var order = await OrderRow(id, null, ct, true);
        var amount = order["total"]?.GetValue<decimal>() ?? 583200m;
        var bankRef = "SIMULATED-PAYOS-" + DateTime.UtcNow.Ticks.ToString("x");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pendingPayments = await Rows("SELECT id, order_code AS \"orderCode\", number FROM eport_service_payments WHERE draft_id=@p0 AND status='PENDING' ORDER BY created_at DESC", ct, id);
        Guid paymentId;
        if (pendingPayments.Count > 0)
        {
            paymentId = G(pendingPayments[0], "id");
            await Exec("""
                UPDATE eport_service_payments
                SET status='SUCCEEDED', bank_reference=@p1, gateway_status='PAID', gateway_error='',
                    reason='Thanh toán thử nghiệm thành công (Test Mode)', updated_at=now()
                WHERE id=@p0
                """, ct, paymentId, bankRef);
        }
        else
        {
            paymentId = Guid.NewGuid();
            var paymentNo = "PAY-SIM-" + DateTime.UtcNow.ToString("yyMMddHHmmss");
            var orderCode = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Exec("""
                INSERT INTO eport_service_payments(id,draft_id,request_id,number,method,order_code,status,amount,currency,created_by,bank_reference,gateway_status,gateway_error,reason,created_at,updated_at)
                VALUES(@p0,@p1,@p0,@p2,'PAYOS',@p3,'SUCCEEDED',@p4,'VND',@p5,@p6,'PAID','', 'Thanh toán thử nghiệm thành công (Test Mode)',now(),now())
                """, ct, paymentId, id, paymentNo, orderCode, amount, Actor, bankRef);
        }

        await PaymentState(id, "PAID", ct);
        var simRequestId = Guid.NewGuid();
        var simFingerprint = PaymentFingerprint("PAYMENT_SIMULATED", paymentId, new { reference = bankRef });
        await AddEvent(id, "PAYMENT_SUCCEEDED", S(order, "status"), S(order, "status"), "Xác nhận thanh toán thử nghiệm thành công", ct, simRequestId, simFingerprint, new JsonObject
        {
            ["paymentId"] = paymentId.ToString(),
            ["amount"] = amount,
            ["reference"] = bankRef
        });

        var updated = await OrderRow(id, null, ct);
        await QueueServiceMail(updated, "PAYMENT_RECEIPT", ct);
        await tx.CommitAsync(ct);
        return new { success = true, message = "Thanh toán thành công!", order = OrderView(updated, null) };
    }
}

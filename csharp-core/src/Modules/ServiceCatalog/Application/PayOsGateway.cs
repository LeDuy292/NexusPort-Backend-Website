using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed record PayOsPaymentLink(long OrderCode, long Amount, string PaymentLinkId, string Status,
    long AmountPaid = 0, long AmountRemaining = 0, string? CheckoutUrl = null, string? QrCode = null,
    string Currency = "VND", JsonArray? Transactions = null);

public sealed record PayOsWebhookData(long OrderCode, long Amount, string PaymentLinkId, string Reference,
    string Currency, string Code, JsonObject Data);

public interface IPayOsGateway
{
    bool Configured { get; }
    Task<PayOsPaymentLink> CreateAsync(long orderCode, long amount, Guid orderId, CancellationToken ct);
    Task<PayOsPaymentLink?> GetAsync(long orderCode, CancellationToken ct);
    Task<PayOsPaymentLink> CancelAsync(long orderCode, string reason, CancellationToken ct);
    PayOsWebhookData VerifyWebhook(JsonObject body);
}

/// <summary>Official payOS v2 requests with a fixed provider origin and signed requests/responses/webhooks.</summary>
public sealed class PayOsGateway(HttpClient http, IConfiguration configuration) : IPayOsGateway
{
    private const string Origin = "https://api-merchant.payos.vn";
    private const long MaxSafeInteger = 9007199254740991;
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private string ClientId => configuration["PAYOS_CLIENT_ID"]?.Trim() ?? "";
    private string ApiKey => configuration["PAYOS_API_KEY"]?.Trim() ?? "";
    private string ChecksumKey => configuration["PAYOS_CHECKSUM_KEY"]?.Trim() ?? "";
    private string ReturnUrl => configuration["PAYOS_RETURN_URL"]?.Trim() ?? "";
    private string CancelUrl => configuration["PAYOS_CANCEL_URL"]?.Trim() ?? "";
    private bool Development => string.Equals(configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase);

    private bool ValidReturnUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Scheme == Uri.UriSchemeHttps || (Development && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    public bool Configured => ClientId.Length > 0 && ApiKey.Length > 0 && ChecksumKey.Length > 0
        && ValidReturnUrl(ReturnUrl) && ValidReturnUrl(CancelUrl);

    private void RequireConfigured()
    {
        if (!Configured) throw new CatalogException("Thanh toán payOS chưa được cấu hình. Liên hệ quản trị viên để cấu hình kênh thanh toán.", 503, "PAYOS_CONFIGURATION_REQUIRED");
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Sorted).ToArray()),
        _ => node?.DeepClone()
    };

    private static string Scalar(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonArray) return Sorted(node)!.ToJsonString(CompactJson);
        if (node is JsonObject) throw new FormatException("Unexpected nested object in payOS signature data.");
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text is "null" or "undefined" ? "" : text;
        return node.ToJsonString(CompactJson);
    }

    public static string Sign(JsonObject data, string checksumKey)
    {
        var canonical = string.Join("&", data.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + Scalar(pair.Value)));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(checksumKey), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static bool VerifySignature(JsonObject data, string signature, string checksumKey)
    {
        if (string.IsNullOrWhiteSpace(checksumKey) || signature.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Sign(data, checksumKey)), Convert.FromHexString(signature)); }
        catch (Exception error) when (error is FormatException or ArgumentException) { return false; }
    }

    private static string Text(JsonObject data, string name)
        => data[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static long Integer(JsonObject data, string name)
    {
        if (data[name] is JsonValue value && value.TryGetValue<long>(out var amount) && amount is >= 0 and <= MaxSafeInteger) return amount;
        throw new CatalogException("payOS trả về giá trị tiền hoặc mã đơn không hợp lệ. Chưa cập nhật kết quả thanh toán.", 502, "PAYOS_INVALID_RESPONSE");
    }

    private static string AddOrder(string url, Guid id) => url + (url.Contains('?') ? "&" : "?") + "orderId=" + id.ToString("D");

    private async Task<JsonObject?> Send(HttpMethod method, string path, JsonObject? body, bool allowNotFound, CancellationToken ct)
    {
        RequireConfigured();
        using var request = new HttpRequestMessage(method, Origin + path);
        request.Headers.Add("x-client-id", ClientId);
        request.Headers.Add("x-api-key", ApiKey);
        if (body != null) request.Content = JsonContent.Create(body);
        // Keep HttpClient.Timeout in force until the small JSON body is buffered.
        // Returning after headers would leave the later JSON read unbounded while
        // the payment service holds its transaction and per-order lock.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new CatalogException("Chưa xác minh được phản hồi payOS. Giữ yêu cầu hiện tại và đồng bộ lại; không tạo yêu cầu thanh toán khác.", 502, "PAYOS_GATEWAY_UNAVAILABLE");
        JsonObject envelope;
        try { envelope = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)) ?? throw new JsonException(); }
        catch (JsonException) { throw new CatalogException("Phản hồi payOS không hợp lệ. Chưa ghi nhận kết quả thanh toán.", 502, "PAYOS_INVALID_RESPONSE"); }
        if (Text(envelope, "code") != "00" || envelope["data"] is not JsonObject data)
            throw new CatalogException("payOS chưa xác nhận yêu cầu. Đồng bộ lại yêu cầu hiện tại hoặc liên hệ quản trị viên.", 502, "PAYOS_GATEWAY_REJECTED");
        if (!VerifySignature(data, Text(envelope, "signature"), ChecksumKey))
            throw new CatalogException("Chữ ký phản hồi payOS không hợp lệ. Chưa cập nhật thanh toán.", 502, "PAYOS_INVALID_SIGNATURE");
        return data;
    }

    private static PayOsPaymentLink Link(JsonObject data, bool creating = false)
    {
        var id = Text(data, "paymentLinkId");
        if (id.Length == 0) id = Text(data, "id");
        if (id.Length is < 1 or > 100) throw new CatalogException("Mã link thanh toán payOS không hợp lệ.", 502, "PAYOS_INVALID_RESPONSE");
        var checkout = Text(data, "checkoutUrl");
        if (checkout.Length > 0 && (!Uri.TryCreate(checkout, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.Host != "pay.payos.vn" || !string.IsNullOrEmpty(url.UserInfo)))
            throw new CatalogException("Đường dẫn thanh toán payOS không hợp lệ.", 502, "PAYOS_INVALID_RESPONSE");
        var amount = Integer(data, "amount");
        var status = Text(data, "status");
        if (status is not ("PENDING" or "PROCESSING" or "PAID" or "CANCELLED" or "EXPIRED"))
            throw new CatalogException("Trạng thái payOS chưa được hỗ trợ. Giữ nguyên yêu cầu để kiểm tra.", 502, "PAYOS_INVALID_RESPONSE");
        var transactions = data["transactions"] is JsonArray array ? array.DeepClone().AsArray()
            : data["transactions"] is JsonObject obj ? new JsonArray(obj.Select(pair => pair.Value?.DeepClone()).ToArray()) : new JsonArray();
        return new(Integer(data, "orderCode"), amount, id, status,
            creating && data["amountPaid"] == null ? 0 : Integer(data, "amountPaid"),
            creating && data["amountRemaining"] == null ? amount : Integer(data, "amountRemaining"),
            checkout.Length > 0 ? checkout : null, Text(data, "qrCode"),
            Text(data, "currency") is { Length: > 0 } currency ? currency : "VND", transactions);
    }

    public async Task<PayOsPaymentLink> CreateAsync(long orderCode, long amount, Guid orderId, CancellationToken ct)
    {
        RequireConfigured();
        if (orderCode is <= 0 or > MaxSafeInteger || amount is <= 0 or > MaxSafeInteger) throw new CatalogException("Số tiền hoặc mã yêu cầu payOS không hợp lệ.");
        var description = "DV" + (orderCode % 10000000).ToString("D7", CultureInfo.InvariantCulture);
        var data = new JsonObject { ["orderCode"] = orderCode, ["amount"] = amount, ["description"] = description,
            ["returnUrl"] = AddOrder(ReturnUrl, orderId), ["cancelUrl"] = AddOrder(CancelUrl, orderId) };
        data["signature"] = Sign(data, ChecksumKey);
        return Link((await Send(HttpMethod.Post, "/v2/payment-requests", data, false, ct))!, true);
    }

    public async Task<PayOsPaymentLink?> GetAsync(long orderCode, CancellationToken ct)
    {
        var data = await Send(HttpMethod.Get, "/v2/payment-requests/" + orderCode.ToString(CultureInfo.InvariantCulture), null, true, ct);
        return data == null ? null : Link(data);
    }

    public async Task<PayOsPaymentLink> CancelAsync(long orderCode, string reason, CancellationToken ct)
        => Link((await Send(HttpMethod.Post, "/v2/payment-requests/" + orderCode.ToString(CultureInfo.InvariantCulture) + "/cancel",
            new JsonObject { ["cancellationReason"] = reason }, false, ct))!);

    public PayOsWebhookData VerifyWebhook(JsonObject body)
    {
        if (ChecksumKey.Length == 0) throw new CatalogException("Chưa cấu hình khóa xác minh webhook payOS.", 503, "PAYOS_CONFIGURATION_REQUIRED");
        if (body["data"] is not JsonObject data || !VerifySignature(data, Text(body, "signature"), ChecksumKey))
            throw new CatalogException("Chữ ký webhook payOS không hợp lệ.", 400, "PAYOS_INVALID_SIGNATURE");
        if (body["success"] is not JsonValue success || !success.TryGetValue<bool>(out var successful) || !successful
            || Text(body, "code") != "00" || Text(data, "code") != "00")
            throw new CatalogException("Webhook không xác nhận giao dịch payOS thành công.", 400, "PAYOS_WEBHOOK_NOT_SUCCESSFUL");
        var reference = Text(data, "reference").Trim();
        var link = Text(data, "paymentLinkId").Trim();
        if (reference.Length is < 1 or > 150 || reference.Any(char.IsControl) || link.Length is < 1 or > 100)
            throw new CatalogException("Tham chiếu webhook payOS không hợp lệ.", 400, "PAYOS_INVALID_WEBHOOK");
        return new(Integer(data, "orderCode"), Integer(data, "amount"), link, reference, Text(data, "currency"), Text(data, "code"), data.DeepClone().AsObject());
    }
}

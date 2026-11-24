using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public interface IServiceWorkflowEmail
{
    Task SendCode(string email, string code, CancellationToken ct);
    Task SendMessage(string email, string subject, string body, string attachmentName, string attachmentHtml, CancellationToken ct)
        => throw new NotSupportedException("Order email delivery is not configured.");
    Task SendMessage(string email, string subject, string body, string attachmentName, byte[]? attachmentBytes, string mimeType, bool isHtmlBody, CancellationToken ct)
        => throw new NotSupportedException("Order email delivery is not configured.");
}

/// <summary>Uses the existing EmailSettings; only successful SMTP delivery is reported as sent.</summary>
public sealed class ServiceWorkflowEmail(IConfiguration configuration) : IServiceWorkflowEmail
{
    public Task SendCode(string email, string code, CancellationToken ct) => Send(email, "NexusPort - Mã xác thực đăng ký giao nhận",
        $"Mã xác thực đăng ký giao nhận của bạn là: {code}\nMã có hiệu lực trong 5 phút, tối đa 5 lần nhập. Không cung cấp mã này cho người khác.\nXác thực email không thay thế kiểm tra hiệu lực Booking/DO/eDO hoặc thông quan.", null, null, null, false, ct);

    public Task SendMessage(string email, string subject, string body, string attachmentName, string attachmentHtml, CancellationToken ct)
        => Send(email, subject, body, attachmentName, attachmentHtml != null ? System.Text.Encoding.UTF8.GetBytes(attachmentHtml) : null, "text/html", false, ct);

    public Task SendMessage(string email, string subject, string body, string attachmentName, byte[]? attachmentBytes, string mimeType, bool isHtmlBody, CancellationToken ct)
        => Send(email, subject, body, attachmentName, attachmentBytes, mimeType, isHtmlBody, ct);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Gửi qua Brevo HTTPS API (cổng 443). Dùng khi nền tảng deploy chặn cổng SMTP đi ra (vd. Railway gói thấp).
    /// Bật bằng EmailSettings:BrevoApiKey; người gửi phải là địa chỉ đã xác minh trong Brevo.
    /// </summary>
    /// <summary>
    /// Gửi qua một Google Apps Script web app gắn với Gmail của hệ thống (HTTPS cổng 443). Dùng khi không có tên miền/SMTP.
    /// Bật bằng EmailSettings:GoogleScriptUrl và EmailSettings:GoogleScriptSecret (mã bí mật khớp với script).
    /// </summary>
    private async Task SendViaGoogleScript(string url, string? secret, string email, string subject, string body, string? attachmentName, byte[]? attachmentBytes, string? mimeType, bool isHtmlBody, CancellationToken ct)
    {
        var settings = configuration.GetSection("EmailSettings");
        var payload = new Dictionary<string, object?>
        {
            ["secret"] = secret ?? "",
            ["to"] = email,
            ["subject"] = subject,
            ["senderName"] = settings["SenderName"] ?? "NexusPort",
            ["body"] = body,
            ["isHtml"] = isHtmlBody,
        };
        if (attachmentBytes is { Length: > 0 })
        {
            payload["attachmentBase64"] = Convert.ToBase64String(attachmentBytes);
            payload["attachmentName"] = attachmentName ?? "Chung-tu.pdf";
            payload["mimeType"] = mimeType ?? "application/pdf";
        }
        try
        {
            using var response = await Http.PostAsync(url.Trim(), new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"), ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var ok = false;
            try { ok = System.Text.Json.JsonDocument.Parse(text).RootElement.TryGetProperty("ok", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True; } catch { }
            if (response.IsSuccessStatusCode && ok) return;
            Console.WriteLine($"[ServiceWorkflowEmail] Google script failed ({(int)response.StatusCode}): {text[..Math.Min(text.Length, 300)]}. Recipient: {email}");
            throw new CatalogException("Không gửi được email xác thực qua cầu gửi Google. Kiểm tra URL, mã bí mật và quyền triển khai của script.", 503, "SERVICE_EMAIL_DELIVERY_FAILED");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[ServiceWorkflowEmail] Google script timeout: {ex.Message}. Recipient: {email}");
            throw new CatalogException("Máy chủ email phản hồi quá chậm. Vui lòng thử lại sau.", 503, "SERVICE_EMAIL_TIMEOUT");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[ServiceWorkflowEmail] Google script unreachable: {ex.Message}. Recipient: {email}");
            throw new CatalogException("Không kết nối được dịch vụ email. Vui lòng thử lại sau.", 503, "SERVICE_EMAIL_DELIVERY_FAILED");
        }
    }

    /// <summary>Gửi qua Resend HTTPS API. Người gửi (EmailSettings:SenderEmail) phải thuộc tên miền đã xác thực trong Resend.</summary>
    private async Task SendViaResend(string apiKey, string email, string subject, string body, string? attachmentName, byte[]? attachmentBytes, bool isHtmlBody, CancellationToken ct)
    {
        var settings = configuration.GetSection("EmailSettings");
        var sender = settings["SenderEmail"] ?? settings["SmtpUser"];
        if (!MailAddress.TryCreate(sender, out _))
            throw new CatalogException("Thiếu EmailSettings:SenderEmail hợp lệ (phải thuộc tên miền đã xác thực trong Resend).", 503, "SERVICE_EMAIL_NOT_CONFIGURED");

        var payload = new Dictionary<string, object?>
        {
            ["from"] = $"{settings["SenderName"] ?? "NexusPort"} <{sender}>",
            ["to"] = new[] { email },
            ["subject"] = subject,
            [isHtmlBody ? "html" : "text"] = body,
        };
        if (attachmentBytes is { Length: > 0 })
            payload["attachments"] = new[] { new { filename = attachmentName ?? "Chung-tu.pdf", content = Convert.ToBase64String(attachmentBytes) } };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", "Bearer " + apiKey.Trim());

        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return;
            var detail = await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[ServiceWorkflowEmail] Resend API failed ({(int)response.StatusCode}): {detail}. Recipient: {email}");
            throw new CatalogException("Không gửi được email xác thực qua Resend. Kiểm tra EmailSettings:ResendApiKey và địa chỉ người gửi thuộc tên miền đã xác thực.", 503, "SERVICE_EMAIL_DELIVERY_FAILED");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[ServiceWorkflowEmail] Resend API timeout: {ex.Message}. Recipient: {email}");
            throw new CatalogException("Máy chủ email phản hồi quá chậm. Vui lòng thử lại sau.", 503, "SERVICE_EMAIL_TIMEOUT");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[ServiceWorkflowEmail] Resend API unreachable: {ex.Message}. Recipient: {email}");
            throw new CatalogException("Không kết nối được dịch vụ email. Vui lòng thử lại sau.", 503, "SERVICE_EMAIL_DELIVERY_FAILED");
        }
    }

    private async Task SendViaBrevo(string apiKey, string email, string subject, string body, string? attachmentName, byte[]? attachmentBytes, string? mimeType, bool isHtmlBody, CancellationToken ct)
    {
        var settings = configuration.GetSection("EmailSettings");
        var sender = settings["SenderEmail"] ?? settings["SmtpUser"];
        if (!MailAddress.TryCreate(sender, out _))
            throw new CatalogException("Thiếu EmailSettings:SenderEmail hợp lệ (phải là địa chỉ đã xác minh trong Brevo).", 503, "SERVICE_EMAIL_NOT_CONFIGURED");

        var payload = new Dictionary<string, object?>
        {
            ["sender"] = new { name = settings["SenderName"] ?? "NexusPort", email = sender },
            ["to"] = new[] { new { email } },
            ["subject"] = subject,
            [isHtmlBody ? "htmlContent" : "textContent"] = body,
        };
        if (attachmentBytes is { Length: > 0 })
            payload["attachment"] = new[] { new { name = attachmentName ?? "Chung-tu.pdf", content = Convert.ToBase64String(attachmentBytes) } };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("api-key", apiKey.Trim());
        request.Headers.Add("accept", "application/json");

        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return;
            var detail = await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[ServiceWorkflowEmail] Brevo API failed ({(int)response.StatusCode}): {detail}. Recipient: {email}");
            throw new CatalogException("Không gửi được email xác thực qua dịch vụ email. Kiểm tra EmailSettings:BrevoApiKey và địa chỉ người gửi đã xác minh trong Brevo.", 503, "SERVICE_EMAIL_DELIVERY_FAILED");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[ServiceWorkflowEmail] Brevo API timeout: {ex.Message}. Recipient: {email}");
            throw new CatalogException("Máy chủ email phản hồi quá chậm. Vui lòng thử lại sau.", 503, "SERVICE_EMAIL_TIMEOUT");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[ServiceWorkflowEmail] Brevo API unreachable: {ex.Message}. Recipient: {email}");
            throw new CatalogException("Không kết nối được dịch vụ email. Vui lòng thử lại sau.", 503, "SERVICE_EMAIL_DELIVERY_FAILED");
        }
    }

    private async Task Send(string email, string subject, string body, string? attachmentName, byte[]? attachmentBytes, string? mimeType, bool isHtmlBody, CancellationToken ct)
    {
        var settings = configuration.GetSection("EmailSettings");

        // Cầu gửi qua Google Apps Script (HTTPS, không cần tên miền hay SMTP)
        var scriptUrl = settings["GoogleScriptUrl"];
        if (!string.IsNullOrWhiteSpace(scriptUrl))
        {
            if (!MailAddress.TryCreate(email, out _))
                throw new CatalogException("Email nhận mã xác thực không hợp lệ.", 422, "SERVICE_ACCOUNT_EMAIL_REQUIRED");
            await SendViaGoogleScript(scriptUrl, settings["GoogleScriptSecret"], email, subject, body, attachmentName, attachmentBytes, mimeType, isHtmlBody, ct);
            return;
        }

        // Resend (HTTPS): EmailSettings:ResendApiKey; người gửi phải thuộc tên miền đã xác thực trong Resend
        var resendKey = settings["ResendApiKey"];
        if (!string.IsNullOrWhiteSpace(resendKey))
        {
            if (!MailAddress.TryCreate(email, out _))
                throw new CatalogException("Email nhận mã xác thực không hợp lệ.", 422, "SERVICE_ACCOUNT_EMAIL_REQUIRED");
            await SendViaResend(resendKey, email, subject, body, attachmentName, attachmentBytes, isHtmlBody, ct);
            return;
        }

        var brevoKey = settings["BrevoApiKey"];
        if (!string.IsNullOrWhiteSpace(brevoKey))
        {
            if (!MailAddress.TryCreate(email, out _))
                throw new CatalogException("Email nhận mã xác thực không hợp lệ.", 422, "SERVICE_ACCOUNT_EMAIL_REQUIRED");
            await SendViaBrevo(brevoKey, email, subject, body, attachmentName, attachmentBytes, mimeType, isHtmlBody, ct);
            return;
        }

        var host = settings["SmtpHost"]; var sender = settings["SenderEmail"];
        var login = settings["SmtpUser"]?.Trim(); var password = settings["SmtpPass"];

        if (string.IsNullOrWhiteSpace(host) || !MailAddress.TryCreate(sender, out _) || string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password) || password.Trim() is "abcdefghijklmnop" or "your-app-password" or "changeme")
        {
            throw new CatalogException("Cấu hình EmailSettings chưa đầy đủ hoặc mật khẩu SMTP còn là giá trị mẫu.", 503, "SERVICE_EMAIL_NOT_CONFIGURED");
        }

        if (!MailAddress.TryCreate(email, out _))
            throw new CatalogException("Email nhận mã xác thực không hợp lệ.", 422, "SERVICE_ACCOUNT_EMAIL_REQUIRED");

        var port = 587;
        if ((!string.IsNullOrWhiteSpace(settings["SmtpPort"]) && !int.TryParse(settings["SmtpPort"], out port)) || port is < 1 or > 65535)
            port = 587;

        var gmail = host.Trim().Equals("smtp.gmail.com", StringComparison.OrdinalIgnoreCase);
        var enableSsl = !bool.TryParse(settings["EnableSsl"], out var ssl) || ssl;

        using var client = new SmtpClient(host.Trim(), port)
        {
            EnableSsl = enableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(login, gmail ? password.Replace(" ", "").Trim() : password),
            Timeout = 15000
        };

        using var message = new MailMessage
        {
            From = new MailAddress(sender!, settings["SenderName"] ?? "NexusPort"),
            Subject = subject,
            IsBodyHtml = isHtmlBody,
            Body = body,
            BodyEncoding = System.Text.Encoding.UTF8,
            SubjectEncoding = System.Text.Encoding.UTF8
        };
        message.To.Add(email);
        if (attachmentBytes != null && attachmentBytes.Length > 0)
        {
            message.Attachments.Add(new Attachment(new MemoryStream(attachmentBytes), attachmentName ?? "Chung-tu.pdf", mimeType ?? "application/pdf"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await client.SendMailAsync(message, timeout.Token);
        }
        catch (Exception ex)
        {
            var isTimeout = ex is OperationCanceledException && !ct.IsCancellationRequested;
            var isSmtp = ex is SmtpException;
            if (isTimeout || isSmtp)
            {
                var env = configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["NODE_ENV"];
                // Chạy trên Railway thì luôn coi là môi trường thật, kể cả khi quên đặt ASPNETCORE_ENVIRONMENT=Production,
                // để không giả lập "đã gửi" khiến người dùng chờ một email không bao giờ tới.
                var onRailway = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT"))
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID"));
                var isProduction = onRailway || string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase);
                var allowFallback = !isProduction
                    || bool.TryParse(configuration["EmailSettings:AllowFallback"], out var fb) && fb
                    || bool.TryParse(configuration["EmailSettings:BypassSmtp"], out var bp) && bp;

                Console.WriteLine($"[ServiceWorkflowEmail] SMTP delivery failed ({ex.GetType().Name}: {ex.Message}). Recipient: {email}");

                if (allowFallback)
                {
                    // Không in nội dung (có mã xác thực) ra log trên môi trường thật
                    Console.WriteLine(isProduction
                        ? $"[ServiceWorkflowEmail] FALLBACK SIMULATION: email to {email} NOT delivered (fallback explicitly enabled)."
                        : $"[ServiceWorkflowEmail] FALLBACK SIMULATION (Development/Sandbox): Accepted message to {email}. Content: {body}");
                    return;
                }
            }

            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new CatalogException("Máy chủ email phản hồi quá chậm. Vui lòng thử lại sau.", 503, "SERVICE_EMAIL_TIMEOUT");
            if (ex is SmtpException)
                throw new CatalogException("Không gửi được email xác thực. Kiểm tra kết nối SMTP và App Password trong EmailSettings; mã chưa được gửi thành công.", 503, "SERVICE_EMAIL_DELIVERY_FAILED");
            throw;
        }
    }
}

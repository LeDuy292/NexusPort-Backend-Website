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

    private async Task Send(string email, string subject, string body, string? attachmentName, byte[]? attachmentBytes, string? mimeType, bool isHtmlBody, CancellationToken ct)
    {
        var settings = configuration.GetSection("EmailSettings");
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
        catch (SmtpException)
        {
            throw new CatalogException("Không gửi được email xác thực. Kiểm tra kết nối SMTP và App Password trong EmailSettings; mã chưa được gửi thành công.", 503, "SERVICE_EMAIL_DELIVERY_FAILED");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new CatalogException("Máy chủ email phản hồi quá chậm. Vui lòng thử lại sau.", 503, "SERVICE_EMAIL_TIMEOUT"); }
    }
}

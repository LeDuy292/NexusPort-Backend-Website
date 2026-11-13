using Microsoft.AspNetCore.Mvc;
using NexusPort.Infrastructure.ExternalServices;
using System.Security.Cryptography;
using System.Text;
using System.Net;

namespace NexusPort.Api.Controllers;

[ApiController]
[Route("internal/account-email")]
public sealed class AccountEmailController(IEmailService email, IConfiguration config) : ControllerBase
{
    public sealed record InvitationEmail(string To, string ActivationUrl);
    [HttpPost]
    public async Task<IActionResult> Send(InvitationEmail input, CancellationToken ct)
    {
        var expected = config["AccountInvitations:ServiceKey"];
        var supplied = Request.Headers["X-Account-Email-Key"].ToString();
        if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
            return Unauthorized();
        var origin = config["AccountInvitations:FrontendOrigin"];
        if (!Uri.TryCreate(input.ActivationUrl, UriKind.Absolute, out var url) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var allowed) ||
            url.GetLeftPart(UriPartial.Authority) != allowed.GetLeftPart(UriPartial.Authority) ||
            url.AbsolutePath != "/activate-account" || !System.Net.Mail.MailAddress.TryCreate(input.To, out _))
            return BadRequest();
        if (string.IsNullOrWhiteSpace(config["EmailSettings:SmtpUser"]) || string.IsNullOrWhiteSpace(config["EmailSettings:SmtpPass"]))
            return StatusCode(503, new { message = "Email service is not configured." });
        await email.SendEmailAsync(input.To, "NexusPort — Liên kết tài khoản tài xế",
            $"<p>Công ty vận tải đã đăng ký tài khoản tài xế cho bạn.</p><p><a href=\"{WebUtility.HtmlEncode(input.ActivationUrl)}\">Xác nhận email và tạo mật khẩu</a></p><p>Liên kết dùng một lần, có hiệu lực 24 giờ. Hãy xác nhận và tạo mật khẩu để đăng nhập. Nếu bạn không yêu cầu, hãy bỏ qua email này.</p>", ct);
        return Ok(new { success = true });
    }
}

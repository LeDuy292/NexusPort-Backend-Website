using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceDraftService
{
    private async Task<bool> DeliverySchema(CancellationToken ct) =>
        (await Rows("SELECT to_regclass('eport_service_email_verifications') IS NOT NULL AS ready", ct))[0]["ready"]!.GetValue<bool>();
    private static string SourceHash(ServiceDraftInput input)
    {
        var code = (input.Flow == "EXPORT"
            ? (input.Source.BookingNumber ?? input.Source.ReferenceNumber ?? input.Source.BookingId?.ToString() ?? "")
            : (input.Source.ReferenceNumber ?? input.Source.BlNumber ?? "")).Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{input.Flow}:{code}")));
    }
    private async Task<string> RegisteredEmail(CancellationToken ct)
    {
        var account = (await Rows("SELECT email FROM users WHERE id=@p0 AND is_active", ct, Actor)).Single();
        var email = S(account, "email").Trim().ToLowerInvariant();
        Require(System.Net.Mail.MailAddress.TryCreate(email, out _), "Tài khoản chưa có email hợp lệ để nhận mã xác thực.", 422, "SERVICE_ACCOUNT_EMAIL_REQUIRED");
        return email;
    }
    private static string HashCode(string code, byte[] salt) => Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(code, salt, 100000, HashAlgorithmName.SHA256, 32));
    private async Task GuardEmailDraft(Guid id, Guid company, CancellationToken ct)
    {
        Require(id != Guid.Empty, "Mã hồ sơ không hợp lệ.");
        var draft = await Find(id, ct);
        if (draft != null) { await EnsureDraftWorkflowOpen(id, ct); Require(await FindRegistration(id, ct) == null, "Đơn đã xác nhận, không cần gửi lại mã.", 409); }
        Require(await DeliverySchema(ct), "Cần triển khai migration 20261110_041 cho quy trình xác thực email.", 503, "SERVICE_DELIVERY_SCHEMA_REQUIRED");
    }
    public async Task<object> SendRegistrationEmail(Guid id, ServiceDraftInput request, CancellationToken ct)
    {
        var company = await Company(ct);
        await GuardEmailDraft(id, company, ct);
        var input = ServiceDraftRules.Normalize(request);
        if (input.Flow == "IMPORT")
            Require(!string.IsNullOrEmpty(input.Source.ReferenceNumber) || !string.IsNullOrEmpty(input.Source.BlNumber), "Nhập số eDO hoặc vận đơn trước khi xác thực.");
        else
        {
            var hasBookingCode = !string.IsNullOrEmpty(input.Source.BookingNumber) || !string.IsNullOrEmpty(input.Source.ReferenceNumber);
            Require(input.Source.BookingId.HasValue || hasBookingCode, "Nhập số vận đơn hàng xuất trước khi xác thực.");
        }
        var email = await RegisteredEmail(ct);
        Require(workflowEmail != null, "Dịch vụ gửi email xác thực chưa sẵn sàng. Vui lòng thử lại sau.", 503, "SERVICE_EMAIL_UNAVAILABLE");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))", ct, "eport-email:" + Actor.ToString("N"));
        var challenge = Guid.NewGuid(); var salt = RandomNumberGenerator.GetBytes(32);
        var code = RandomNumberGenerator.GetInt32(1000000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        await Exec("UPDATE eport_service_email_verifications SET invalidated_at=now() WHERE (draft_id=@p0 OR actor_id=@p1) AND invalidated_at IS NULL", ct, id, Actor);
        await Exec("""
            INSERT INTO eport_service_email_verifications(id,draft_id,actor_id,carrier_id,email,source_hash,code_hash,salt,source_payload)
            VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,CAST(@p8 AS jsonb))
            """, ct, challenge, id, Actor, company, email, SourceHash(input), HashCode(code, salt), Convert.ToHexString(salt),
            JsonSerializer.Serialize(new { input.Flow, input.MainServiceId, input.Source }, Json));
        await tx.CommitAsync(ct);
        try { await workflowEmail!.SendCode(email, code, ct); }
        catch (Exception ex)
        {
            await Exec("UPDATE eport_service_email_verifications SET invalidated_at=now() WHERE id=@p0", CancellationToken.None, challenge);
            Console.WriteLine($"[ServiceDraftService] SendCode failed ({ex.GetType().Name}: {ex.Message})");
            // Giữ nguyên lỗi cụ thể (thiếu cấu hình, timeout, SMTP từ chối...) để người dùng biết cách xử lý
            if (ex is CatalogException) throw;
            throw new CatalogException("Không gửi được mã xác thực đến email. Vui lòng kiểm tra cấu hình email và thử lại.", 503, "SERVICE_EMAIL_DELIVERY_FAILED");
        }
        var at = email.IndexOf('@');
        return new { challengeId = challenge, maskedEmail = email[..1] + "***" + email[at..], expiresInSeconds = 300, retryAfterSeconds = 60 };
    }
    public async Task<object> VerifyRegistrationEmail(Guid id, ServiceEmailCodeInput request, CancellationToken ct)
    {
        var company = await Company(ct);
        await GuardEmailDraft(id, company, ct);
        Require(Regex.IsMatch(request.Code ?? "", "^[0-9]{6}$"), "Mã xác thực gồm 6 chữ số.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await Rows("""
            SELECT *,expires_at>now() AS live FROM eport_service_email_verifications
            WHERE id=@p0 AND (draft_id=@p1 OR actor_id=@p2) FOR UPDATE
            """, ct, request.ChallengeId, id, Actor);
        Require(rows.Count == 1, "Không tìm thấy yêu cầu xác thực.", 404);
        var row = rows[0];
        Require(row["live"]!.GetValue<bool>() && row["invalidated_at"] == null, "Mã đã hết hạn hoặc bị khóa. Vui lòng gửi mã mới.", 422, "SERVICE_EMAIL_EXPIRED");
        var matches = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(S(row, "code_hash")),
            Convert.FromHexString(HashCode(request.Code!, Convert.FromHexString(S(row, "salt")))));
        await Exec("UPDATE eport_service_email_verifications SET attempts=attempts+1,verified_at=CASE WHEN @p1 THEN COALESCE(verified_at,now()) ELSE verified_at END WHERE id=@p0", ct, request.ChallengeId, matches);
        await tx.CommitAsync(ct);
        Require(matches, "Mã xác thực không đúng. Tối đa 5 lần nhập.", 422, "SERVICE_EMAIL_INVALID");
        return new { verified = true, validForSeconds = 3600 };
    }
    private async Task<bool> HasSourceEmail(Guid id, ServiceDraftInput input, Guid company, CancellationToken ct)
    {
        var email = await RegisteredEmail(ct);
        var hash = SourceHash(input);
        return (await Rows("""
            SELECT EXISTS(
                SELECT 1 FROM eport_service_email_verifications
                WHERE (draft_id = @p0 OR (actor_id = @p1 AND email = @p2))
                    AND (source_hash = @p3 OR draft_id = @p0)
                    AND invalidated_at IS NULL
                    AND verified_at > now() - interval '60 minutes'
            ) AS verified
            """, ct, id, Actor, email, hash))[0]["verified"]!.GetValue<bool>();
    }
}

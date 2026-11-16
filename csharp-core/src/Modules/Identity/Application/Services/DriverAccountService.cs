using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.ExternalServices;
using NexusPort.Modules.Identity.Application.DTOs;
using NexusPort.Shared.Exceptions;
using Npgsql;

namespace NexusPort.Modules.Identity.Application.Services;

public interface IAccountInvitationEmail
{
    Task SendAsync(string email, string activationUrl, CancellationToken token);
}

public sealed class AccountInvitationEmail(IEmailService email, IConfiguration config) : IAccountInvitationEmail
{
    public async Task SendAsync(string to, string url, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(config["EmailSettings:SmtpUser"]) || string.IsNullOrWhiteSpace(config["EmailSettings:SmtpPass"]))
            throw new ValidationException("Email", "Chưa cấu hình dịch vụ gửi email xác nhận.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await email.SendEmailAsync(to, "NexusPort — Liên kết tài khoản tài xế",
                $"<p>Công ty vận tải đã đăng ký tài khoản tài xế cho bạn.</p><p><a href=\"{System.Net.WebUtility.HtmlEncode(url)}\">Xác nhận email và tạo mật khẩu</a></p><p>Liên kết dùng một lần, có hiệu lực 24 giờ.</p>", timeout.Token);
        }
        catch (Exception ex) when (ex is System.Net.Mail.SmtpException || (ex is OperationCanceledException && !token.IsCancellationRequested))
        { throw new ValidationException("Email", "Không gửi được email xác nhận. Vui lòng thử lại."); }
    }
}

public sealed class DriverAccountService(AppDbContext db, IAccountInvitationEmail email, IConfiguration config)
{
    public async Task<object> CreateAsync(Guid actor, DriverAccountDto input, CancellationToken ct)
    {
        input.Username = input.Username.Trim(); input.Email = input.Email.Trim().ToLowerInvariant();
        input.FullName = input.FullName.Trim(); input.LicenseNumber = input.LicenseNumber.Trim();
        if (input.FullName.Length < 2 || input.LicenseNumber.Length == 0)
            throw new ValidationException("Profile", "Vui lòng kiểm tra họ tên và giấy phép lái xe.");
        foreach (var image in new[] { input.PhotoUrl, input.IdCardFrontUrl, input.IdCardBackUrl, input.LicenseImageUrl, input.LicenseBackImageUrl })
            if (!string.IsNullOrEmpty(image) && !image.StartsWith("NexusPort/drivers/", StringComparison.Ordinal)
                && !(Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
                throw new ValidationException("Image", "Ảnh phải là ảnh tài xế đã tải lên.");
        // Same bcrypt format as the existing login service. No usable password before email confirmation.
        var password = BCrypt.Net.BCrypt.HashPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), 12);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('driver-account-provisioning'))", ct);
            var memberships = await db.Database.SqlQueryRaw<Guid>("""
                SELECT cu.carrier_id AS "Value" FROM carrier_users cu JOIN users u ON u.id=cu.user_id
                WHERE u.id={0} AND u.role='Transport Company' AND u.is_active FOR SHARE OF u
                """, actor).ToListAsync(ct);
            if (memberships.Count != 1) throw new ForbiddenException("Tài khoản phải thuộc đúng một công ty vận tải.");
            var company = memberships[0];
            if (input.DriverId.HasValue)
            {
                var profiles = await db.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM drivers WHERE id={0} AND carrier_id={1} AND user_id IS NULL FOR UPDATE", input.DriverId.Value, company).ToListAsync(ct);
                if (profiles.Count != 1) throw new ConflictException("Hồ sơ không thuộc công ty hoặc đã có tài khoản.");
            }
            var exists = await db.Database.SqlQueryRaw<int>("""
                SELECT 1 AS "Value" FROM users WHERE lower(username)=lower({0}) OR lower(email)=lower({1})
                  OR lower(username)=lower({1}) OR lower(email)=lower({0}) LIMIT 1
                """, input.Username, input.Email).ToListAsync(ct);
            if (exists.Count > 0) throw new ConflictException("Tên đăng nhập hoặc email đã được sử dụng.");
            var user = Guid.NewGuid(); var driver = input.DriverId ?? Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users(id,username,email,password,role,full_name,is_active,invitation_pending,created_at,updated_at)
                VALUES({user},{input.Username},{input.Email},{password},'Driver',{input.FullName},false,true,now(),now())
                """, ct);
            if (input.DriverId.HasValue)
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE drivers SET user_id={user} WHERE id={driver} AND carrier_id={company}", ct);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO drivers(id,carrier_id,user_id,full_name,phone,id_card_number,license_number,photo_url,
                      id_card_front_url,id_card_back_url,license_image_url,license_back_image_url,id_card_expiry_date,license_expiry_date,status,created_at)
                    VALUES({driver},{company},{user},{input.FullName},{input.Phone},{input.IdCardNumber},{input.LicenseNumber},{input.PhotoUrl},
                      {input.IdCardFrontUrl},{input.IdCardBackUrl},{input.LicenseImageUrl},{input.LicenseBackImageUrl},
                      {input.IdCardExpiryDate?.ToUniversalTime()},{input.LicenseExpiryDate?.ToUniversalTime()},'inactive',now())
                    """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO carrier_users(carrier_id,user_id) VALUES({company},{user}) ON CONFLICT DO NOTHING", ct);
            // Keep the existing audit table and invitation format; existing emailed links stay valid.
            var profile = JsonSerializer.Serialize(input, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var record = (await db.Database.SqlQueryRaw<Guid>("""
                INSERT INTO account_requests(role,carrier_id,driver_id,requested_by,username,email,profile,status,user_id)
                VALUES('Driver',{0},{1},{2},{3},{4},{5}::jsonb,'approved',{6}) RETURNING id AS "Value"
                """, company, driver, actor, input.Username, input.Email, profile, user).ToListAsync(ct)).Single();
            var expires = await InviteAsync(record, user, input.Email, ct);
            await tx.CommitAsync(ct);
            return new { driverId = driver, userId = user, input.Username, input.Email, emailVerified = false, invitationPending = true, expiresAt = expires };
        }
        catch (Exception ex) when (ex is PostgresException { SqlState: "23505" } || ex.InnerException is PostgresException { SqlState: "23505" })
        { throw new ConflictException("Thông tin tài xế hoặc tài khoản đã tồn tại."); }
    }

    private async Task<DateTime> InviteAsync(Guid record, Guid user, string address, CancellationToken ct)
    {
        var origin = config["AccountInvitations:FrontendOrigin"] ?? config["ACCOUNT_ACTIVATION_ORIGIN"];
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var frontend) || frontend.Scheme is not ("http" or "https"))
            throw new ValidationException("Email", "Chưa cấu hình địa chỉ trang xác nhận tài khoản.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = TokenHash(token); var expires = DateTime.UtcNow.AddDays(1);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE account_invitations SET used_at=now() WHERE user_id={user} AND used_at IS NULL", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO account_invitations(user_id,request_id,token_hash,expires_at) VALUES({user},{record},{hash},{expires})", ct);
        await email.SendAsync(address, new Uri(frontend, "/activate-account").AbsoluteUri + "#token=" + token, ct);
        return expires;
    }

    public async Task<object> ResendAsync(Guid actor, Guid driver, bool admin, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Database.SqlQueryRaw<InvitationAccount>("""
            SELECT u.id AS "Id",u.email AS "Email",u.username AS "Username",u.email_verified_at AS "VerifiedAt",
              u.invitation_pending AS "Pending",u.is_active AS "Active",d.carrier_id AS "CarrierId"
            FROM drivers d JOIN users u ON u.id=d.user_id WHERE d.id={0} AND u.role='Driver'
              AND ({2} OR EXISTS(SELECT 1 FROM carrier_users cu WHERE cu.user_id={1} AND cu.carrier_id=d.carrier_id)) FOR UPDATE OF u
            """, driver, actor, admin).ToListAsync(ct);
        var account = rows.SingleOrDefault() ?? throw new NotFoundException("Tài xế", driver);
        if (account.VerifiedAt.HasValue) throw new ConflictException("Email tài xế đã được xác minh.");
        if (!account.Active && !account.Pending) throw new ConflictException("Tài khoản đã bị chặn bởi quản trị viên.");
        var records = await db.Database.SqlQueryRaw<Guid>("""
            SELECT id AS "Value" FROM account_requests WHERE user_id={0} AND status='approved' AND lower(email)=lower({1}) ORDER BY created_at DESC LIMIT 1
            """, account.Id, account.Email).ToListAsync(ct);
        var record = records.FirstOrDefault();
        if (record == Guid.Empty)
            record = (await db.Database.SqlQueryRaw<Guid>("""
                INSERT INTO account_requests(role,carrier_id,driver_id,requested_by,username,email,profile,status,user_id)
                VALUES('Driver',{0},{1},{2},{3},{4},jsonb_build_object('driverId',{1}::text),'approved',{5}) RETURNING id AS "Value"
                """, account.CarrierId, driver, actor, account.Username, account.Email, account.Id).ToListAsync(ct)).Single();
        var expires = await InviteAsync(record, account.Id, account.Email, ct);
        await tx.CommitAsync(ct);
        return new { sent = true, expiresAt = expires };
    }

    public async Task<object> ActivateAsync(ActivateDriverAccountDto input, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(input.Password) > 72)
            throw new ValidationException("Password", "Mật khẩu tối đa 72 byte UTF-8.");
        var password = BCrypt.Net.BCrypt.HashPassword(input.Password, 12);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Database.SqlQueryRaw<Guid>("""
            SELECT i.user_id AS "Value" FROM account_invitations i JOIN users u ON u.id=i.user_id
            JOIN account_requests r ON r.id=i.request_id AND r.user_id=u.id AND lower(r.email)=lower(u.email)
            WHERE i.token_hash={0} AND i.used_at IS NULL AND i.expires_at>now() AND u.role='Driver'
              AND (u.invitation_pending OR (u.is_active AND u.email_verified_at IS NULL)) FOR UPDATE OF i,u
            """, TokenHash(input.Token)).ToListAsync(ct);
        if (rows.Count != 1) throw new ValidationException("Token", "Liên kết không hợp lệ, đã dùng hoặc đã hết hạn 24 giờ.");
        var user = rows[0];
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE users SET password={password},invitation_pending=false,is_active=true,email_verified_at=now(),
              session_id=gen_random_uuid()::text,updated_at=now() WHERE id={user}
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE drivers SET status='active' WHERE user_id={user} AND status='inactive' AND EXISTS(
              SELECT 1 FROM account_requests r WHERE r.user_id={user} AND r.status='approved' AND NOT (r.profile ? 'driverId'))
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE account_invitations SET used_at=now() WHERE user_id={user} AND used_at IS NULL", ct);
        await tx.CommitAsync(ct);
        return new { activated = true, emailVerified = true };
    }

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public sealed class InvitationAccount
{
    public Guid Id { get; set; }
    public Guid CarrierId { get; set; }
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTime? VerifiedAt { get; set; }
    public bool Pending { get; set; }
    public bool Active { get; set; }
}

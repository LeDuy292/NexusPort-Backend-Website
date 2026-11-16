using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusPort.Modules.Driver.Application.DTOs;
using NexusPort.Modules.Driver.Application.Interfaces;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NexusPort.Modules.Driver.Application.Services;
using NexusPort.Infrastructure.ExternalServices;

namespace NexusPort.Modules.Driver.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class DriverController : ControllerBase
{
    private readonly IDriverService _service;
    private readonly IOcrService _ocrService;
    private readonly IS3StorageService _s3StorageService;

    public DriverController(IDriverService service, IOcrService ocrService, IS3StorageService s3StorageService)
    {
        _service = service;
        _ocrService = ocrService;
        _s3StorageService = s3StorageService;
    }

    private Task<Guid?> GetCarrierIdAsync(CancellationToken token) =>
        NexusPort.Infrastructure.Authentication.CarrierAccess.ResolveAsync(
            HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>(), User, token);

    private bool IsPortStaff()
    {
        return User.Claims.Any(c => c.Type == ClaimTypes.Role && 
            (c.Value.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || 
             c.Value.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("dispatcher", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("operation", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("Gate Officer", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("Gate", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("Yard Operator", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("Yard Staff", StringComparison.OrdinalIgnoreCase)));
    }

    [HttpPost("extract-cccd")]
    [Consumes("multipart/form-data")]
    [AllowAnonymous]
    public async Task<IActionResult> ExtractCccd(IFormFile image, CancellationToken cancellationToken)
    {
        if (image == null || image.Length == 0) return BadRequest(new { message = "No image provided" });

        try
        {
            var ext = Path.GetExtension(image.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var originalFileName = $"cccd_front_{Guid.NewGuid()}{ext}";
            
            // 1. Upload original CCCD image directly to AWS S3
            string idCardFrontUrl;
            using (var uploadStream = image.OpenReadStream())
            {
                idCardFrontUrl = await _s3StorageService.UploadFileAsync(
                    uploadStream, 
                    originalFileName, 
                    image.ContentType ?? "image/jpeg", 
                    "drivers", 
                    cancellationToken
                );
            }

            // 2. Perform AI OCR extraction (best effort)
            OcrRecognitionResponse? data = null;
            try
            {
                data = await _ocrService.ExtractIdCardAsync(image, cancellationToken);
            }
            catch (Exception ocrEx)
            {
                Console.WriteLine("OCR CCCD parsing warning: " + ocrEx.Message);
            }

            // 3. Upload cropped face image directly to AWS S3 if found
            string? faceUrl = null;
            if (data?.FaceImageBytes != null && data.FaceImageBytes.Length > 0)
            {
                try
                {
                    var faceFileName = $"face_{Guid.NewGuid()}.jpg";
                    faceUrl = await _s3StorageService.UploadBytesAsync(
                        data.FaceImageBytes, 
                        faceFileName, 
                        "image/jpeg", 
                        "drivers", 
                        cancellationToken
                    );
                }
                catch (Exception faceEx)
                {
                    Console.WriteLine("Face upload warning: " + faceEx.Message);
                }
            }

            return Ok(new
            {
                fullName = data?.Name,
                idCardNumber = data?.Id,
                dob = data?.Dob,
                sex = data?.Sex,
                address = data?.Address,
                expiryDate = data?.ExpiryDate,
                faceImageUrl = faceUrl,
                photoUrl = faceUrl,
                idCardFrontUrl = idCardFrontUrl,
                side = data?.Side
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXTRACT CCCD S3 ERROR: " + ex.ToString());
            return StatusCode(500, new { message = $"Lỗi tải ảnh lên AWS S3: {ex.Message}" });
        }
    }

    [HttpPost("extract-gplx")]
    [Consumes("multipart/form-data")]
    [AllowAnonymous]
    public async Task<IActionResult> ExtractGplx(IFormFile image, CancellationToken cancellationToken)
    {
        if (image == null || image.Length == 0) return BadRequest(new { message = "No image provided" });

        try
        {
            var ext = Path.GetExtension(image.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var originalFileName = $"gplx_{Guid.NewGuid()}{ext}";
            
            // 1. Upload original GPLX image directly to AWS S3
            string licenseImageUrl;
            using (var uploadStream = image.OpenReadStream())
            {
                licenseImageUrl = await _s3StorageService.UploadFileAsync(
                    uploadStream, 
                    originalFileName, 
                    image.ContentType ?? "image/jpeg", 
                    "drivers", 
                    cancellationToken
                );
            }

            // 2. Perform AI OCR extraction (best effort)
            OcrRecognitionResponse? data = null;
            try
            {
                data = await _ocrService.ExtractDriverLicenseAsync(image, cancellationToken);
            }
            catch (Exception ocrEx)
            {
                Console.WriteLine("OCR GPLX parsing warning: " + ocrEx.Message);
            }

            // 3. Upload cropped face image directly to AWS S3 if found
            string? faceUrl = null;
            if (data?.FaceImageBytes != null && data.FaceImageBytes.Length > 0)
            {
                try
                {
                    var faceFileName = $"face_{Guid.NewGuid()}.jpg";
                    faceUrl = await _s3StorageService.UploadBytesAsync(
                        data.FaceImageBytes, 
                        faceFileName, 
                        "image/jpeg", 
                        "drivers", 
                        cancellationToken
                    );
                }
                catch (Exception faceEx)
                {
                    Console.WriteLine("Face upload warning: " + faceEx.Message);
                }
            }

            return Ok(new
            {
                fullName = data?.Name,
                dob = data?.Dob,
                licenseNumber = data?.Id,
                expiryDate = data?.ExpiryDate,
                licenseImageUrl = licenseImageUrl,
                faceImageUrl = faceUrl,
                photoUrl = faceUrl,
                side = data?.Side
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXTRACT GPLX S3 ERROR: " + ex.ToString());
            return StatusCode(500, new { message = "Upload/OCR process failed. Please try again or fill manually." });
        }
    }

    [HttpPost("upload-document-back")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadDocumentBack(IFormFile image, CancellationToken cancellationToken)
    {
        if (image == null || image.Length == 0)
            return BadRequest(new { message = "No image file provided." });

        try
        {
            string url = string.Empty;
            using (var uploadStream = image.OpenReadStream())
            {
                url = await _s3StorageService.UploadFileAsync(
                    uploadStream, 
                    image.FileName, 
                    image.ContentType, 
                    "drivers",
                    cancellationToken);
            }

            return Ok(new
            {
                success = true,
                imageUrl = url
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("UPLOAD DOCUMENT BACK ERROR: " + ex.ToString());
            return StatusCode(500, new { message = "Upload process failed." });
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DriverDto>>> GetAll([FromQuery] DriverFilterDto filter, CancellationToken cancellationToken)
    {
        if (!IsPortStaff())
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (userCarrierId == null) return StatusCode(403, new { message = "Tài khoản cần liên kết đúng một công ty vận tải." });
            filter.CarrierId = userCarrierId; // Force filter by their own company
        }

        var items = await _service.GetAllAsync(filter, cancellationToken);

        var accountLinks = await HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>()
            .Database.SqlQueryRaw<DriverAccountLink>("""
                SELECT d.id AS "Id", d.user_id AS "UserId",u.username AS "Username",u.email AS "Email",u.is_active AS "AccountActive",
                  (SELECT b.booking_code FROM bookings b WHERE b.carrier_id=d.carrier_id AND b.driver_id=d.id
                    AND b.status::text NOT IN ('completed','canceled','rejected','expired') ORDER BY b.appointment_start LIMIT 1) AS "ActiveBookingCode",
                  u.invitation_pending AS "InvitationPending",u.email_verified_at AS "EmailVerifiedAt",
                  (SELECT i.expires_at FROM account_invitations i WHERE i.user_id=u.id AND i.used_at IS NULL
                    ORDER BY i.created_at DESC LIMIT 1) AS "InvitationExpiresAt"
                FROM drivers d LEFT JOIN users u ON u.id=d.user_id WHERE d.id=ANY({0})
                """, items.Select(x => x.Id).ToArray()).ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var item in items)
        {
            if (!accountLinks.TryGetValue(item.Id, out var link)) continue;
            item.UserId = link.UserId; item.Username = link.Username; item.Email = link.Email;
            item.AccountActive = link.AccountActive; item.ActiveBookingCode = link.ActiveBookingCode;
            item.InvitationPending = link.InvitationPending; item.EmailVerifiedAt = link.EmailVerifiedAt;
            item.EmailVerified = link.EmailVerifiedAt.HasValue; item.InvitationExpiresAt = link.InvitationExpiresAt;
        }

        // Map Carrier Name
        var db = HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>();
        var conn = db.Database.GetDbConnection();
        bool openedByUs = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            openedByUs = true;
        }
        try
        {
            using var command = conn.CreateCommand();
            command.Parameters.Clear();
            var carrierIds = command.CreateParameter(); carrierIds.ParameterName = "carrierIds";
            carrierIds.Value = items.Select(x => x.CarrierId).Distinct().ToArray(); command.Parameters.Add(carrierIds);
            command.CommandText = "SELECT id, company_name FROM carriers WHERE id=ANY(@carrierIds)";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var carrierMap = new Dictionary<Guid, string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                carrierMap[reader.GetGuid(0)] = reader.GetString(1);
            }

            foreach(var item in items)
            {
                if (carrierMap.TryGetValue(item.CarrierId, out var cName))
                {
                    item.CarrierName = cName;
                }
            }
        }
        finally
        {
            if (openedByUs)
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        return Ok(items);
    }

    [HttpGet("photos")]
    public async Task<IActionResult> GetPhotos([FromQuery] Guid[] ids, CancellationToken ct)
    {
        if (ids.Length > 100) return BadRequest(new { message = "Mỗi lần chỉ tải tối đa 100 ảnh." });
        var db = HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>();
        var company = IsPortStaff() ? null : await GetCarrierIdAsync(ct);
        if (!IsPortStaff() && company == null) return Forbid();
        var rows = await db.Set<NexusPort.Modules.Driver.Domain.Entities.Driver>().AsNoTracking()
            .Where(d => ids.Contains(d.Id) && (company == null || d.CarrierId == company))
            .Select(d => new { d.Id, d.PhotoUrl }).ToListAsync(ct);
        var photos = new Dictionary<Guid, string>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(row.PhotoUrl)) photos[row.Id] = _s3StorageService.GetPresignedUrl(row.PhotoUrl);
        }
        return Ok(photos);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DriverDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();

        if (!IsPortStaff())
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (item.CarrierId != userCarrierId) return Forbid();
        }

        return Ok(item);
    }

    [HttpPost]
    public ActionResult Create() => StatusCode(410, new { message = "Vui lòng tạo tài khoản từ trang quản lý tài xế của công ty vận tải." });

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<DriverDto>> Update(Guid id, [FromBody] UpdateDriverDto dto, CancellationToken cancellationToken)
    {
        var existing = await _service.GetByIdAsync(id, cancellationToken);
        if (existing == null) return NotFound();

        if (!IsPortStaff())
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (existing.CarrierId != userCarrierId) return Forbid();
        }

        try
        {
            var item = await _service.UpdateAsync(id, dto, cancellationToken);
            return Ok(item);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> ToggleStatus(Guid id, [FromBody] string status, CancellationToken cancellationToken)
    {
        var existing = await _service.GetByIdAsync(id, cancellationToken);
        if (existing == null) return NotFound();

        if (!IsPortStaff())
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (existing.CarrierId != userCarrierId) return Forbid();
        }

        try
        {
            await _service.ToggleStatusAsync(id, status, cancellationToken);

            if (status.Equals("inactive", StringComparison.OrdinalIgnoreCase) || status.Equals("banned", StringComparison.OrdinalIgnoreCase))
            {
                var db = HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>();
                var conn = db.Database.GetDbConnection();
                bool openedByUs = false;
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await db.Database.OpenConnectionAsync(cancellationToken);
                    openedByUs = true;
                }
                try
                {
                    using (var command = conn.CreateCommand())
                    {
                        command.CommandText = "UPDATE trucks SET driver_id = NULL WHERE driver_id = @id";
                        var param = command.CreateParameter();
                        param.ParameterName = "@id";
                        param.Value = id;
                        command.Parameters.Add(param);
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
                finally
                {
                    if (openedByUs)
                    {
                        await db.Database.CloseConnectionAsync();
                    }
                }
            }

            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

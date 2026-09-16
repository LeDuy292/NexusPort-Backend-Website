using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusPort.Modules.Driver.Application.DTOs;
using NexusPort.Modules.Driver.Application.Interfaces;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NexusPort.Modules.Driver.Application.Services;

namespace NexusPort.Modules.Driver.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class DriverController : ControllerBase
{
    private readonly IDriverService _service;
    private readonly IOcrService _ocrService;

    public DriverController(IDriverService service, IOcrService ocrService)
    {
        _service = service;
        _ocrService = ocrService;
    }

    private Guid? GetCarrierIdFromToken()
    {
        var carrierIdClaim = User.Claims.FirstOrDefault(c => c.Type == "CarrierId")?.Value;
        if (Guid.TryParse(carrierIdClaim, out var carrierId)) return carrierId;
        return null;
    }

    private bool IsPortStaff()
    {
        return User.Claims.Any(c => c.Type == ClaimTypes.Role && 
            (c.Value.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || 
             c.Value.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("dispatcher", StringComparison.OrdinalIgnoreCase) ||
             c.Value.Equals("operation", StringComparison.OrdinalIgnoreCase)));
    }

    [HttpPost("extract-cccd")]
    [Consumes("multipart/form-data")]
    [AllowAnonymous]
    public async Task<IActionResult> ExtractCccd(IFormFile image, CancellationToken cancellationToken)
    {
        if (image == null || image.Length == 0) return BadRequest(new { message = "No image provided" });

        try
        {
            var env = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            var uploadsFolder = Path.Combine(env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads", "drivers");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);
            
            // Save original image
            var originalFileName = $"cccd_front_{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
            var originalFilePath = Path.Combine(uploadsFolder, originalFileName);
            using (var stream = new FileStream(originalFilePath, FileMode.Create))
            {
                await image.CopyToAsync(stream, cancellationToken);
            }
            var idCardFrontUrl = $"/uploads/drivers/{originalFileName}";

            var data = await _ocrService.ExtractIdCardAsync(image, cancellationToken);
            if (data == null)
            {
                return BadRequest(new { message = "Failed to extract ID card data" });
            }

            string? faceUrl = null;

            if (data.FaceImageBytes != null && data.FaceImageBytes.Length > 0)
            {
                var fileName = $"face_{Guid.NewGuid()}.jpg";
                var filePath = Path.Combine(uploadsFolder, fileName);
                
                await System.IO.File.WriteAllBytesAsync(filePath, data.FaceImageBytes, cancellationToken);
                
                faceUrl = $"/uploads/drivers/{fileName}";
            }

            return Ok(new
            {
                fullName = data.Name,
                idCardNumber = data.Id,
                dob = data.Dob,
                sex = data.Sex,
                address = data.Address,
                faceImageUrl = faceUrl,
                idCardFrontUrl = idCardFrontUrl
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXTRACT CCCD ERROR: " + ex.ToString());
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPost("extract-gplx")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ExtractGplx(IFormFile image, CancellationToken cancellationToken)
    {
        if (image == null || image.Length == 0) return BadRequest(new { message = "No image provided" });

        try
        {
            var env = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            var uploadsFolder = Path.Combine(env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads", "drivers");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);
            
            // Save original image
            var originalFileName = $"gplx_{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
            var originalFilePath = Path.Combine(uploadsFolder, originalFileName);
            using (var stream = new FileStream(originalFilePath, FileMode.Create))
            {
                await image.CopyToAsync(stream, cancellationToken);
            }
            var licenseImageUrl = $"/uploads/drivers/{originalFileName}";

            var data = await _ocrService.ExtractDriverLicenseAsync(image, cancellationToken);
            if (data == null)
            {
                return BadRequest(new { message = "Failed to extract Driver License data" });
            }
            string? faceUrl = null;
            if (data.FaceImageBytes != null && data.FaceImageBytes.Length > 0)
            {
                var fileName = $"face_{Guid.NewGuid()}.jpg";
                var filePath = Path.Combine(uploadsFolder, fileName);
                await System.IO.File.WriteAllBytesAsync(filePath, data.FaceImageBytes, cancellationToken);
                faceUrl = $"/uploads/drivers/{fileName}";
            }

            return Ok(new
            {
                fullName = data.Name,
                dob = data.Dob,
                licenseNumber = data.Id,
                licenseImageUrl = licenseImageUrl,
                faceImageUrl = faceUrl
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXTRACT GPLX ERROR: " + ex.ToString());
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DriverDto>>> GetAll([FromQuery] DriverFilterDto filter, CancellationToken cancellationToken)
    {
        if (!IsPortStaff())
        {
            var userCarrierId = GetCarrierIdFromToken();
            if (userCarrierId == null) return Forbid();
            filter.CarrierId = userCarrierId; // Force filter by their own company
        }

        var items = await _service.GetAllAsync(filter, cancellationToken);

        // Map Carrier Name
        var db = HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT id, company_name FROM carriers";
        await db.Database.OpenConnectionAsync(cancellationToken);
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

        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DriverDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();

        if (!IsPortStaff())
        {
            var userCarrierId = GetCarrierIdFromToken();
            if (item.CarrierId != userCarrierId) return Forbid();
        }

        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<DriverDto>> Create([FromBody] CreateDriverDto dto, [FromQuery] Guid? carrierId, CancellationToken cancellationToken)
    {
        Guid targetCarrierId;
        if (IsPortStaff())
        {
            if (carrierId.HasValue && carrierId.Value != Guid.Empty)
            {
                targetCarrierId = carrierId.Value;
            }
            else
            {
                var db = HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>();
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT id FROM carriers WHERE company_name ILIKE '%Tiên Sa%' OR company_name ILIKE '%Cảng%' LIMIT 1";
                await db.Database.OpenConnectionAsync(cancellationToken);
                var result = await command.ExecuteScalarAsync(cancellationToken);
                if (result == null) 
                {
                    return BadRequest(new { message = "Không tìm thấy công ty 'Cảng Tiên Sa' trong hệ thống. Vui lòng tạo một Công ty Vận tải tên là 'Cảng Tiên Sa' trước!" });
                }
                targetCarrierId = (Guid)result;
            }
        }
        else
        {
            var userCarrierId = GetCarrierIdFromToken();
            if (userCarrierId == null) return Forbid();
            targetCarrierId = userCarrierId.Value;
        }

        try
        {
            var item = await _service.CreateAsync(targetCarrierId, dto, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<DriverDto>> Update(Guid id, [FromBody] UpdateDriverDto dto, CancellationToken cancellationToken)
    {
        var existing = await _service.GetByIdAsync(id, cancellationToken);
        if (existing == null) return NotFound();

        if (!IsPortStaff())
        {
            var userCarrierId = GetCarrierIdFromToken();
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
            var userCarrierId = GetCarrierIdFromToken();
            if (existing.CarrierId != userCarrierId) return Forbid();
        }

        try
        {
            await _service.ToggleStatusAsync(id, status, cancellationToken);

            if (status.Equals("inactive", StringComparison.OrdinalIgnoreCase) || status.Equals("banned", StringComparison.OrdinalIgnoreCase))
            {
                var db = HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>();
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "UPDATE trucks SET driver_id = NULL WHERE driver_id = @id";
                var param = command.CreateParameter();
                param.ParameterName = "@id";
                param.Value = id;
                command.Parameters.Add(param);
                await db.Database.OpenConnectionAsync(cancellationToken);
                await command.ExecuteNonQueryAsync(cancellationToken);
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

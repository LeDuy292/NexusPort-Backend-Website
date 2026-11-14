using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using NexusPort.Modules.Vehicle.Application.DTOs;
using NexusPort.Modules.Vehicle.Application.Interfaces;
using NexusPort.Modules.Driver.Application.Services;
using System.Security.Claims;

namespace NexusPort.Modules.Vehicle.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class VehicleController : ControllerBase
{
    private readonly IVehicleService _service;
    private readonly IOcrService _ocrService;
    private readonly IWebHostEnvironment _env;
    private readonly NexusPort.Infrastructure.ExternalServices.IS3StorageService _s3StorageService;

    public VehicleController(IVehicleService service, IOcrService ocrService, IWebHostEnvironment env, NexusPort.Infrastructure.ExternalServices.IS3StorageService s3StorageService)
    {
        _service = service;
        _ocrService = ocrService;
        _env = env;
        _s3StorageService = s3StorageService;
    }

    private bool IsPortStaff()
    {
        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (string.IsNullOrEmpty(role)) return false;
        return role.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("Dispatcher", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("Operation", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("Gate Officer", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("Gate", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("Yard Operator", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("Yard Staff", StringComparison.OrdinalIgnoreCase);
    }

    private Task<Guid?> GetCarrierIdAsync(CancellationToken token) =>
        NexusPort.Infrastructure.Authentication.CarrierAccess.ResolveAsync(
            HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>(), User, token);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VehicleDto>>> GetAll([FromQuery] VehicleFilterDto filter, CancellationToken cancellationToken)
    {
        if (!IsPortStaff())
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (userCarrierId == null) return StatusCode(403, new { message = "You don't have a valid Carrier Profile." });
            filter.CarrierId = userCarrierId; // Force filter
        }

        var items = (await _service.GetAllAsync(filter, cancellationToken)).ToList();

        var driverMap = new Dictionary<Guid, string>();
        var carrierMap = new Dictionary<Guid, string>();

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
                var driverIds = command.CreateParameter(); driverIds.ParameterName = "driverIds";
                driverIds.Value = items.Where(x => x.DriverId.HasValue).Select(x => x.DriverId!.Value).Distinct().ToArray(); command.Parameters.Add(driverIds);
                command.CommandText = "SELECT id, full_name FROM drivers WHERE id=ANY(@driverIds)";
                using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                        {
                            driverMap[reader.GetGuid(0)] = reader.GetString(1);
                        }
                    }
                }

                command.Parameters.Clear();
                var carrierIds = command.CreateParameter(); carrierIds.ParameterName = "carrierIds";
                carrierIds.Value = items.Select(x => x.CarrierId).Distinct().ToArray(); command.Parameters.Add(carrierIds);
                command.CommandText = "SELECT id, company_name FROM carriers WHERE id=ANY(@carrierIds)";
                using (var carrierReader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await carrierReader.ReadAsync(cancellationToken))
                    {
                        if (!carrierReader.IsDBNull(0) && !carrierReader.IsDBNull(1))
                        {
                            carrierMap[carrierReader.GetGuid(0)] = carrierReader.GetString(1);
                        }
                    }
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

        foreach (var item in items)
        {
            if (item.DriverId.HasValue && driverMap.TryGetValue(item.DriverId.Value, out var driverName))
            {
                item.DriverName = driverName;
            }
            if (carrierMap.TryGetValue(item.CarrierId, out var carrierName))
            {
                item.CarrierName = carrierName;
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
        var rows = await db.Set<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>().AsNoTracking()
            .Where(v => ids.Contains(v.Id) && (company == null || v.CarrierId == company))
            .Select(v => new { v.Id, v.PhotoUrl }).ToListAsync(ct);
        var photos = new Dictionary<Guid, string>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(row.PhotoUrl)) photos[row.Id] = _s3StorageService.GetPresignedUrl(row.PhotoUrl);
        }
        return Ok(photos);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<VehicleDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();

        if (!IsPortStaff())
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (userCarrierId == null || item.CarrierId != userCarrierId) return StatusCode(403, new { message = "Access denied." });
        }

        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<VehicleDto>> Create([FromBody] CreateVehicleDto dto, [FromQuery] Guid? carrierId, CancellationToken cancellationToken)
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
                var portId = await GetPortCarrierIdAsync(cancellationToken);
                if (portId == null) 
                {
                    return BadRequest(new { message = "Không tìm thấy công ty 'Cảng Tiên Sa' trong hệ thống. Vui lòng tạo một Công ty Vận tải tên là 'Cảng Tiên Sa' trước!" });
                }
                targetCarrierId = portId.Value;
            }
        }
        else
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (userCarrierId == null) return StatusCode(403, new { message = "You don't have a valid Carrier Profile." });
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

    [HttpPost("upload-photo")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadPhoto([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0) return BadRequest(new { message = "No file provided" });

        try
        {
            var imageUrl = await _s3StorageService.UploadFileAsync(file.OpenReadStream(), file.FileName, file.ContentType, "vehicles", cancellationToken);
            return Ok(new { ImageUrl = imageUrl });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Lỗi tải ảnh lên AWS S3: {ex.Message}" });
        }
    }

    [HttpPost("extract-registration")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ExtractRegistration([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0) return BadRequest(new { message = "Vui lòng chọn ảnh Cà Vẹt (Giấy Đăng Ký Xe)." });

        try
        {
            var result = await _ocrService.ExtractVehicleRegistrationAsync(file, cancellationToken);
            var imageUrl = await _s3StorageService.UploadFileAsync(file.OpenReadStream(), file.FileName, file.ContentType, "vehicles", cancellationToken);

            if (result == null || string.IsNullOrEmpty(result.PlateNumber))
            {
                return Ok(new
                {
                    PlateNumber = "",
                    ImageUrl = imageUrl,
                    IsSuccess = false,
                    Message = "Không thể nhận diện được biển số. Vui lòng nhập tay."
                });
            }

            return Ok(new
            {
                PlateNumber = result.PlateNumber,
                Brand = result.Brand,
                ImageUrl = imageUrl,
                IsSuccess = true,
                Message = "Nhận diện Cà Vẹt thành công."
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Lỗi xử lý OCR: {ex.Message}" });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<VehicleDto>> Update(Guid id, [FromBody] UpdateVehicleDto dto, CancellationToken cancellationToken)
    {
        var existing = await _service.GetByIdAsync(id, cancellationToken);
        if (existing == null) return NotFound();

        if (IsPortStaff())
        {
            var portCarrierId = await GetPortCarrierIdAsync(cancellationToken);
            if (portCarrierId == null || existing.CarrierId != portCarrierId.Value)
            {
                return StatusCode(403, new { message = "Admin/Dispatcher chỉ được phép sửa xe thuộc sở hữu của Cảng Tiên Sa." });
            }
        }
        else
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (userCarrierId == null || existing.CarrierId != userCarrierId) return StatusCode(403, new { message = "Bạn chỉ được phép sửa xe của công ty mình." });
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> ToggleStatus(Guid id, [FromBody] string status, CancellationToken cancellationToken)
    {
        var existing = await _service.GetByIdAsync(id, cancellationToken);
        if (existing == null) return NotFound();

        if (IsPortStaff())
        {
            var portCarrierId = await GetPortCarrierIdAsync(cancellationToken);
            if (portCarrierId == null || existing.CarrierId != portCarrierId.Value)
            {
                return StatusCode(403, new { message = "Admin/Dispatcher chỉ được phép sửa xe thuộc sở hữu của Cảng Tiên Sa." });
            }
        }
        else
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (userCarrierId == null || existing.CarrierId != userCarrierId) return StatusCode(403, new { message = "Bạn chỉ được phép sửa xe của công ty mình." });
        }

        try
        {
            await _service.ToggleStatusAsync(id, status, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPatch("{id:guid}/assign-driver")]
    public async Task<IActionResult> AssignDriver(Guid id, [FromBody] AssignDriverDto dto, CancellationToken cancellationToken)
    {
        var existing = await _service.GetByIdAsync(id, cancellationToken);
        if (existing == null) return NotFound();

        if (IsPortStaff())
        {
            // Allowed to edit all vehicles
        }
        else
        {
            var userCarrierId = await GetCarrierIdAsync(cancellationToken);
            if (userCarrierId == null || existing.CarrierId != userCarrierId) return StatusCode(403, new { message = "Bạn chỉ được phép sửa xe của công ty mình." });
        }

        try
        {
            await _service.AssignDriverAsync(id, dto, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
    private async Task<Guid?> GetPortCarrierIdAsync(CancellationToken cancellationToken)
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
                command.CommandText = "SELECT id FROM carriers WHERE company_name ILIKE '%Tiên Sa%' OR company_name ILIKE '%Cảng%' LIMIT 1";
                var result = await command.ExecuteScalarAsync(cancellationToken);
                return result != null ? (Guid)result : null;
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
}

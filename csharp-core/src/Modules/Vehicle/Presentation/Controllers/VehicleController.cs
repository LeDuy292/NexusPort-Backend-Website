using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusPort.Modules.Vehicle.Application.DTOs;
using NexusPort.Modules.Vehicle.Application.Interfaces;
using System.Security.Claims;

namespace NexusPort.Modules.Vehicle.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class VehicleController : ControllerBase
{
    private readonly IVehicleService _service;

    public VehicleController(IVehicleService service)
    {
        _service = service;
    }

    private bool IsPortStaff()
    {
        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        return role == "Administrator" || role == "Dispatcher" || role == "Operation";
    }

    private Guid? GetCarrierIdFromToken()
    {
        var carrierIdStr = User.FindFirst("CarrierId")?.Value;
        if (Guid.TryParse(carrierIdStr, out var carrierId)) return carrierId;
        return null;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VehicleDto>>> GetAll([FromQuery] VehicleFilterDto filter, CancellationToken cancellationToken)
    {
        if (!IsPortStaff())
        {
            var userCarrierId = GetCarrierIdFromToken();
            if (userCarrierId == null) return StatusCode(403, new { message = "You don't have a valid Carrier Profile." });
            filter.CarrierId = userCarrierId; // Force filter
        }

        var items = (await _service.GetAllAsync(filter, cancellationToken)).ToList();

        // Map Driver Name and Carrier Name
        var db = HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT id, full_name FROM drivers";
        await db.Database.OpenConnectionAsync(cancellationToken);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var driverMap = new Dictionary<Guid, string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            driverMap[reader.GetGuid(0)] = reader.GetString(1);
        }
        await reader.CloseAsync();

        command.CommandText = "SELECT id, company_name FROM carriers";
        using var carrierReader = await command.ExecuteReaderAsync(cancellationToken);
        var carrierMap = new Dictionary<Guid, string>();
        while (await carrierReader.ReadAsync(cancellationToken))
        {
            carrierMap[carrierReader.GetGuid(0)] = carrierReader.GetString(1);
        }
        await carrierReader.CloseAsync();

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

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<VehicleDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();

        if (!IsPortStaff())
        {
            var userCarrierId = GetCarrierIdFromToken();
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
            var userCarrierId = GetCarrierIdFromToken();
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
            var userCarrierId = GetCarrierIdFromToken();
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
            var userCarrierId = GetCarrierIdFromToken();
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
            var portCarrierId = await GetPortCarrierIdAsync(cancellationToken);
            if (portCarrierId == null || existing.CarrierId != portCarrierId.Value)
            {
                return StatusCode(403, new { message = "Admin/Dispatcher chỉ được phép sửa xe thuộc sở hữu của Cảng Tiên Sa." });
            }
        }
        else
        {
            var userCarrierId = GetCarrierIdFromToken();
            if (userCarrierId == null || existing.CarrierId != userCarrierId) return StatusCode(403, new { message = "Bạn chỉ được phép sửa xe của công ty mình." });
        }

        if (dto.DriverId.HasValue)
        {
            var db = HttpContext.RequestServices.GetRequiredService<NexusPort.Infrastructure.Database.AppDbContext>();
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT status FROM drivers WHERE id = @id";
            var param = command.CreateParameter();
            param.ParameterName = "@id";
            param.Value = dto.DriverId.Value;
            command.Parameters.Add(param);
            
            await db.Database.OpenConnectionAsync(cancellationToken);
            var driverStatusObj = await command.ExecuteScalarAsync(cancellationToken);
            if (driverStatusObj == null) return NotFound(new { message = "Không tìm thấy tài xế." });
            
            var driverStatus = driverStatusObj.ToString()?.ToLower();
            if (driverStatus == "inactive" || driverStatus == "banned")
            {
                return BadRequest(new { message = "Tài xế đang ở trạng thái tạm nghỉ hoặc bị đình chỉ. Không thể phân công!" });
            }
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
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT id FROM carriers WHERE company_name ILIKE '%Tiên Sa%' OR company_name ILIKE '%Cảng%' LIMIT 1";
        await db.Database.OpenConnectionAsync(cancellationToken);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null ? (Guid)result : null;
    }
}

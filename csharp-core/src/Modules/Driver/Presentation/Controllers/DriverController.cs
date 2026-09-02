using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Driver.Application.DTOs;
using NexusPort.Modules.Driver.Application.Interfaces;
using System.Security.Claims;

namespace NexusPort.Modules.Driver.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class DriverController : ControllerBase
{
    private readonly IDriverService _service;

    public DriverController(IDriverService service)
    {
        _service = service;
    }

    private Guid? GetCarrierIdFromToken()
    {
        var carrierIdClaim = User.Claims.FirstOrDefault(c => c.Type == "CarrierId")?.Value;
        if (Guid.TryParse(carrierIdClaim, out var carrierId)) return carrierId;
        return null;
    }

    private bool IsAdmin()
    {
        return User.Claims.Any(c => c.Type == ClaimTypes.Role && (c.Value.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || c.Value.Equals("admin", StringComparison.OrdinalIgnoreCase)));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DriverDto>>> GetAll([FromQuery] DriverFilterDto filter, CancellationToken cancellationToken)
    {
        if (!IsAdmin())
        {
            var userCarrierId = GetCarrierIdFromToken();
            if (userCarrierId == null) return Forbid();
            filter.CarrierId = userCarrierId; // Force filter by their own company
        }

        var items = await _service.GetAllAsync(filter, cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DriverDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();

        if (!IsAdmin())
        {
            var userCarrierId = GetCarrierIdFromToken();
            if (item.CarrierId != userCarrierId) return Forbid();
        }

        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<DriverDto>> Create([FromBody] CreateDriverDto dto, CancellationToken cancellationToken)
    {
        if (IsAdmin())
        {
            return Forbid("Administrator is not allowed to create drivers. Only Transport Company can create drivers.");
        }

        var userCarrierId = GetCarrierIdFromToken();
        if (userCarrierId == null) return Forbid();
        
        Guid targetCarrierId = userCarrierId.Value;

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

        if (!IsAdmin())
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

        if (!IsAdmin())
        {
            var userCarrierId = GetCarrierIdFromToken();
            if (existing.CarrierId != userCarrierId) return Forbid();
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
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

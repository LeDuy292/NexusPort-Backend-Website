using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Vessel.Application.DTOs;
using NexusPort.Modules.Vessel.Application.Interfaces;

namespace NexusPort.Modules.Vessel.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class VesselController : ControllerBase
{
    private readonly IVesselService _service;

    public VesselController(IVesselService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VesselDto>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _service.GetAllAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<VesselDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<VesselDto>> Create([FromBody] CreateVesselDto dto, CancellationToken cancellationToken)
    {
        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }
}

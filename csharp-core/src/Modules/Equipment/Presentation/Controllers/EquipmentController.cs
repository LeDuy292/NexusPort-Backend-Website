using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Equipment.Application.DTOs;
using NexusPort.Modules.Equipment.Application.Interfaces;

namespace NexusPort.Modules.Equipment.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class EquipmentController : ControllerBase
{
    private readonly IEquipmentService _service;

    public EquipmentController(IEquipmentService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EquipmentDto>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _service.GetAllAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EquipmentDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<EquipmentDto>> Create([FromBody] CreateEquipmentDto dto, CancellationToken cancellationToken)
    {
        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }
}

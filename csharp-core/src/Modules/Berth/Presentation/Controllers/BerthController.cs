using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Berth.Application.DTOs;
using NexusPort.Modules.Berth.Application.Interfaces;

namespace NexusPort.Modules.Berth.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class BerthController : ControllerBase
{
    private readonly IBerthService _service;

    public BerthController(IBerthService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BerthDto>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _service.GetAllAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BerthDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<BerthDto>> Create([FromBody] CreateBerthDto dto, CancellationToken cancellationToken)
    {
        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }
}

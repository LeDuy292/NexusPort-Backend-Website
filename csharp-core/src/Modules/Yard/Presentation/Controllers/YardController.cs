using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;

namespace NexusPort.Modules.Yard.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class YardController : ControllerBase
{
    private readonly IYardService _service;

    public YardController(IYardService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<YardBlockDto>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _service.GetAllAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<YardBlockDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<YardBlockDto>> Create([FromBody] CreateYardBlockDto dto, CancellationToken cancellationToken)
    {
        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }
}

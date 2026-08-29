using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Gate.Application.DTOs;
using NexusPort.Modules.Gate.Application.Interfaces;

namespace NexusPort.Modules.Gate.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class GateController : ControllerBase
{
    private readonly IGateService _service;

    public GateController(IGateService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GateTransactionDto>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _service.GetAllAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GateTransactionDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<GateTransactionDto>> Create([FromBody] CreateGateTransactionDto dto, CancellationToken cancellationToken)
    {
        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }
}

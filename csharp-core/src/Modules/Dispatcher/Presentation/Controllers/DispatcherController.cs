using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Dispatcher.Application.DTOs;
using NexusPort.Modules.Dispatcher.Application.Interfaces;

namespace NexusPort.Modules.Dispatcher.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class DispatcherController : ControllerBase
{
    private readonly IDispatcherService _service;

    public DispatcherController(IDispatcherService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkOrderDto>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _service.GetAllAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkOrderDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<WorkOrderDto>> Create([FromBody] CreateWorkOrderDto dto, CancellationToken cancellationToken)
    {
        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }
}

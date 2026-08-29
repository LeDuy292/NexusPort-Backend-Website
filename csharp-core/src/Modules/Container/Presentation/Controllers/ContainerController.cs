using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Container.Application.DTOs;
using NexusPort.Modules.Container.Application.Interfaces;

namespace NexusPort.Modules.Container.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ContainerController : ControllerBase
{
    private readonly IContainerService _service;

    public ContainerController(IContainerService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ContainerDto>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _service.GetAllAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContainerDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<ContainerDto>> Create([FromBody] CreateContainerDto dto, CancellationToken cancellationToken)
    {
        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }
}

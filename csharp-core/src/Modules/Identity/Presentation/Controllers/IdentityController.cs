using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Identity.Application.DTOs;
using NexusPort.Modules.Identity.Application.Interfaces;

namespace NexusPort.Modules.Identity.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class IdentityController : ControllerBase
{
    private readonly IIdentityService _service;

    public IdentityController(IIdentityService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> GetAll(CancellationToken cancellationToken)
    {
        var items = await _service.GetAllAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserDto dto, CancellationToken cancellationToken)
    {
        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }
}

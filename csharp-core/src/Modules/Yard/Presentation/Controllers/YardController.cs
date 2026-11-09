using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;

namespace NexusPort.Modules.Yard.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class YardController : ControllerBase
{
    private readonly IYardService _service;

    public YardController(IYardService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<YardBlockDto>>> GetAll(CancellationToken cancellationToken) => Ok(await _service.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<YardBlockDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await _service.GetByIdAsync(id, cancellationToken);
        return item == null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<YardBlockDto>> Create([FromBody] CreateYardBlockDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var item = await _service.CreateAsync(dto, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("Block/{id:guid}/Reserve")]
    public async Task<ActionResult<YardSlotDto>> ReserveSlot(Guid id, CancellationToken cancellationToken)
    {
        var slot = await _service.ReserveSlotAsync(id, cancellationToken);
        if (slot == null)
            return BadRequest(new { message = "No empty slots available in this block." });
            
        return Ok(slot);
    }

    [HttpPost("Slots/{id:guid}/Reserve")]
    public async Task<ActionResult<YardSlotDto>> ReserveSpecificSlot(Guid id, CancellationToken cancellationToken)
    {
        var slot = await _service.ReserveSpecificSlotAsync(id, cancellationToken);
        if (slot == null)
            return BadRequest(new { message = "Slot not found or is not empty." });
            
        return Ok(slot);
    }

    [HttpGet("Map")]
    public async Task<ActionResult<IReadOnlyList<YardBlockDto>>> GetYardMap(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetYardMapAsync(cancellationToken));
    }

    [HttpGet("Block/{id:guid}/Slots")]
    public async Task<ActionResult<IReadOnlyList<YardSlotDto>>> GetBlockSlots(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await _service.GetBlockSlotsAsync(id, cancellationToken));
    }

    [HttpPut("Container/{containerId:guid}/Location")]
    public async Task<ActionResult> UpdateContainerLocation(Guid containerId, [FromBody] Guid slotId, CancellationToken cancellationToken)
    {
        try
        {
            await _service.UpdateContainerLocationAsync(containerId, slotId, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("Slots/{id:guid}/toggle-maintenance")]
    public async Task<ActionResult> ToggleSlotMaintenance(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _service.ToggleSlotMaintenanceAsync(id, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Completes a yard operation and queues a realtime driver notification.</summary>
    [HttpPost("operations/{operationId:guid}/complete")]
    public async Task<ActionResult<YardOperationCompletionDto>> CompleteOperation(Guid operationId, [FromBody] CompleteYardOperationDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.CompleteOperationAsync(operationId, dto, cancellationToken);
            return result.DeliveryStatus == "Published" ? Ok(result) : Accepted(result);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new { message = exception.Message });
        }
    }
}

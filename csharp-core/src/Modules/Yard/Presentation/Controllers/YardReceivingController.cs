using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;

namespace NexusPort.Modules.Yard.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class YardReceivingController : ControllerBase
{
    private readonly IYardReceivingService _service;

    public YardReceivingController(IYardReceivingService service)
    {
        _service = service;
    }

    /// <summary>Check & verify Container ID and Seal against expected database values</summary>
    [HttpPost("verify")]
    public async Task<ActionResult<YardContainerVerificationResultDto>> VerifyContainerAndSeal(
        [FromBody] VerifyYardContainerDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _service.VerifyContainerAndSealAsync(dto, cancellationToken);
        return Ok(result);
    }

    /// <summary>Confirm receiving container into yard with inspection condition and timestamp</summary>
    [HttpPost]
    public async Task<ActionResult<YardReceiptDto>> CreateReceipt(
        [FromBody] CreateYardReceiptDto dto,
        CancellationToken cancellationToken)
    {
        var userId = User?.Identity?.Name ?? "YardStaff-01";
        var receipt = await _service.CreateReceiptAsync(dto, userId, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = receipt.Id }, receipt);
    }

    /// <summary>Get list of all yard receiving inspection records</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<YardReceiptDto>>> GetAll(
        [FromQuery] string? blockCode,
        CancellationToken cancellationToken)
    {
        var list = await _service.GetReceiptsAsync(blockCode, cancellationToken);
        return Ok(list);
    }

    /// <summary>Get detail of a specific yard receiving record</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<YardReceiptDto>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var item = await _service.GetReceiptByIdAsync(id, cancellationToken);
        return item == null ? NotFound() : Ok(item);
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Yard.Preparation.Application.DTOs;
using NexusPort.Modules.Yard.Preparation.Application.Services;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Yard.Preparation.Presentation;

[ApiController]
[Route("api/yard/slots")]
[Route("api/v1/yard/slots")]
public class YardStackController : ControllerBase
{
    private readonly IPreparationService _service;

    public YardStackController(IPreparationService service)
    {
        _service = service;
    }

    /// <summary>
    /// Xem toàn bộ các ô và container trong cùng một stack (cùng dãy, cùng ô) theo thứ tự từ tầng cao xuống tầng thấp.
    /// </summary>
    [HttpGet("{id:guid}/stack")]
    [Authorize(Roles = "Dispatcher,YardStaff,Yard Staff,Yard Operator,Administrator,Admin")]
    public async Task<ActionResult<List<StackSlotDto>>> GetStack(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var stack = await _service.GetStackContainersAsync(id, cancellationToken);
            return Ok(stack);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy ô bãi", detail: ex.Message);
        }
    }
}

using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Yard.Preparation.Application.DTOs;
using NexusPort.Modules.Yard.Preparation.Application.Services;
using NexusPort.Modules.Yard.Preparation.Domain;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Yard.Preparation.Presentation;

[ApiController]
[Route("api/work-orders")]
[Route("api/v1/work-orders")]
public class WorkOrderController : ControllerBase
{
    private readonly IPreparationService _service;
    private readonly IValidator<AssignWorkOrderRequestDto> _assignValidator;
    private readonly IValidator<CompleteWorkOrderRequestDto> _completeValidator;
    private readonly IValidator<FailWorkOrderRequestDto> _failValidator;

    public WorkOrderController(
        IPreparationService service,
        IValidator<AssignWorkOrderRequestDto> assignValidator,
        IValidator<CompleteWorkOrderRequestDto> completeValidator,
        IValidator<FailWorkOrderRequestDto> failValidator)
    {
        _service = service;
        _assignValidator = assignValidator;
        _completeValidator = completeValidator;
        _failValidator = failValidator;
    }

    /// <summary>
    /// Danh sách lệnh làm hàng (WorkOrder) được giao cho mình hoặc thiết bị của mình, lọc theo trạng thái.
    /// </summary>
    [HttpGet]
    [Authorize(Roles = "YardStaff,Yard Staff,Yard Operator,Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<PagedResult<WorkOrderDto>>> GetWorkOrders(
        [FromQuery] WorkOrderStatus? status,
        [FromQuery] Guid? assigneeId,
        [FromQuery] Guid? equipmentId,
        [FromQuery] WorkOrderType? type,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _service.GetWorkOrdersAsync(status, assigneeId, equipmentId, type, pageNumber, pageSize, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Điều độ gán người và thiết bị thực hiện lệnh làm hàng.
    /// </summary>
    [HttpPost("{id:guid}/assign")]
    [Authorize(Roles = "Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<WorkOrderDto>> Assign(
        Guid id,
        [FromBody] AssignWorkOrderRequestDto dto,
        CancellationToken cancellationToken)
    {
        var valResult = await _assignValidator.ValidateAsync(dto, cancellationToken);
        if (!valResult.IsValid)
        {
            return Problem(
                statusCode: 422,
                title: "Dữ liệu gán lệnh không hợp lệ",
                detail: string.Join("; ", valResult.Errors.Select(e => e.ErrorMessage)));
        }

        try
        {
            var result = await _service.AssignWorkOrderAsync(id, dto, cancellationToken);
            return Ok(result);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy lệnh làm hàng", detail: ex.Message);
        }
        catch (ConflictException ex)
        {
            return Problem(statusCode: 409, title: "Xung đột thao tác gán lệnh", detail: ex.Message);
        }
    }

    /// <summary>
    /// Nhân viên bãi bắt đầu thực hiện lệnh (kiểm tra container hiện đang ở đúng ô xuất phát FromSlot).
    /// </summary>
    [HttpPost("{id:guid}/start")]
    [Authorize(Roles = "YardStaff,Yard Staff,Yard Operator,Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<WorkOrderDto>> Start(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.StartWorkOrderAsync(id, cancellationToken);
            return Ok(result);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy lệnh làm hàng", detail: ex.Message);
        }
        catch (ConflictException ex)
        {
            return Problem(statusCode: 409, title: "Sai lệch vị trí hoặc trạng thái", detail: ex.Message);
        }
    }

    /// <summary>
    /// Nhân viên bãi hoàn thành lệnh làm hàng: gửi số container quét được và ô đích thực tế (nếu khác ô kế hoạch).
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [Authorize(Roles = "YardStaff,Yard Staff,Yard Operator,Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<WorkOrderDto>> Complete(
        Guid id,
        [FromBody] CompleteWorkOrderRequestDto dto,
        CancellationToken cancellationToken)
    {
        var valResult = await _completeValidator.ValidateAsync(dto, cancellationToken);
        if (!valResult.IsValid)
        {
            return Problem(
                statusCode: 422,
                title: "Dữ liệu hoàn tất lệnh không hợp lệ",
                detail: string.Join("; ", valResult.Errors.Select(e => e.ErrorMessage)));
        }

        try
        {
            var result = await _service.CompleteWorkOrderAsync(id, dto, cancellationToken);
            return Ok(result);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy", detail: ex.Message);
        }
        catch (ConflictException ex)
        {
            return Problem(statusCode: 409, title: "Không thể hoàn thành lệnh", detail: ex.Message);
        }
    }

    /// <summary>
    /// Nhân viên bãi báo lỗi khi thực hiện lệnh làm hàng (kèm lý do cụ thể).
    /// </summary>
    [HttpPost("{id:guid}/fail")]
    [Authorize(Roles = "YardStaff,Yard Staff,Yard Operator,Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<WorkOrderDto>> Fail(
        Guid id,
        [FromBody] FailWorkOrderRequestDto dto,
        CancellationToken cancellationToken)
    {
        var valResult = await _failValidator.ValidateAsync(dto, cancellationToken);
        if (!valResult.IsValid)
        {
            return Problem(
                statusCode: 422,
                title: "Dữ liệu báo lỗi không hợp lệ",
                detail: string.Join("; ", valResult.Errors.Select(e => e.ErrorMessage)));
        }

        try
        {
            var result = await _service.FailWorkOrderAsync(id, dto, cancellationToken);
            return Ok(result);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy", detail: ex.Message);
        }
        catch (ConflictException ex)
        {
            return Problem(statusCode: 409, title: "Lỗi thao tác", detail: ex.Message);
        }
    }
}

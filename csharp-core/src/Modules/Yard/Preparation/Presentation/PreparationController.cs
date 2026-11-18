using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Modules.Yard.Preparation.Application.DTOs;
using NexusPort.Modules.Yard.Preparation.Application.Services;
using NexusPort.Modules.Yard.Preparation.Domain;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Yard.Preparation.Presentation;

[ApiController]
[Route("api/preparation")]
[Route("api/v1/preparation")]
public class PreparationController : ControllerBase
{
    private readonly IPreparationService _service;
    private readonly IValidator<CancelTaskRequestDto> _cancelValidator;
    private readonly IValidator<ConfirmReadyRequestDto> _confirmReadyValidator;
    private readonly IValidator<PreparationConfigDto> _configValidator;

    public PreparationController(
        IPreparationService service,
        IValidator<CancelTaskRequestDto> cancelValidator,
        IValidator<ConfirmReadyRequestDto> confirmReadyValidator,
        IValidator<PreparationConfigDto> configValidator)
    {
        _service = service;
        _cancelValidator = cancelValidator;
        _confirmReadyValidator = confirmReadyValidator;
        _configValidator = configValidator;
    }

    /// <summary>
    /// Danh sách lệnh chuẩn bị container (Lọc theo trạng thái, ngày hẹn, số container, khu bãi, chiều làm hàng Inbound/Outbound, phân trang).
    /// </summary>
    [HttpGet("tasks")]
    [Authorize(Roles = "Dispatcher,YardStaff,Yard Staff,Yard Operator,Administrator,Admin")]
    public async Task<ActionResult<PagedResult<PreparationTaskDto>>> GetTasks(
        [FromQuery] PreparationTaskStatus? status,
        [FromQuery] DateTimeOffset? targetDate,
        [FromQuery] string? containerNo,
        [FromQuery] string? blockCode,
        [FromQuery] PreparationDirection? direction,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _service.GetTasksAsync(status, targetDate, containerNo, blockCode, direction, pageNumber, pageSize, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Tạo lệnh chuẩn bị xuất container xếp tàu (Chiều xuất) dựa trên sơ đồ & danh sách xếp tàu.
    /// </summary>
    [HttpPost("tasks/outbound")]
    [Authorize(Roles = "Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<PreparationTaskDto>> CreateOutboundTask(
        [FromBody] CreateOutboundPreparationTaskRequestDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = await _service.CreateOutboundPreparationTaskAsync(dto, cancellationToken);
            return CreatedAtAction(nameof(GetTaskById), new { id = task.Id }, task);
        }
        catch (NexusPort.Shared.Exceptions.ValidationException ex)
        {
            return Problem(statusCode: 400, title: "Dữ liệu không hợp lệ", detail: ex.Message);
        }
        catch (ConflictException ex)
        {
            return Problem(statusCode: 409, title: "Xung đột nghiệp vụ", detail: ex.Message);
        }
    }

    /// <summary>
    /// Đối soát 5 điều kiện tiên quyết trước khi đưa container vào danh sách chuẩn bị xuất tàu.
    /// </summary>
    [HttpGet("tasks/validate-outbound/{containerNo}")]
    [Authorize(Roles = "Dispatcher,YardStaff,Yard Staff,Yard Operator,Administrator,Admin")]
    public async Task<ActionResult<OutboundEligibilityCheckResultDto>> ValidateOutbound(
        string containerNo,
        [FromQuery] string? voyageCode,
        CancellationToken cancellationToken)
    {
        var result = await _service.ValidateOutboundEligibilityAsync(containerNo, voyageCode, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Chi tiết lệnh chuẩn bị: container, vị trí hiện tại, danh sách container chắn, các WorkOrder, lịch sử di chuyển.
    /// </summary>
    [HttpGet("tasks/{id:guid}")]
    [Authorize(Roles = "Dispatcher,YardStaff,Yard Staff,Yard Operator,Administrator,Admin")]
    public async Task<ActionResult<PreparationTaskDetailDto>> GetTaskById(Guid id, CancellationToken cancellationToken)
    {
        var task = await _service.GetTaskByIdAsync(id, cancellationToken);
        if (task == null)
        {
            return Problem(
                statusCode: 404,
                title: "Không tìm thấy dữ liệu",
                detail: $"Lệnh chuẩn bị có mã '{id}' không tồn tại trong hệ thống.");
        }

        return Ok(task);
    }

    /// <summary>
    /// Điều độ lập lại kế hoạch khi có thay đổi bãi hoặc vị trí container.
    /// </summary>
    [HttpPost("tasks/{id:guid}/replan")]
    [Authorize(Roles = "Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<PreparationTaskDto>> ReplanTask(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var task = await _service.ReplanTaskAsync(id, cancellationToken);
            return Ok(task);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy", detail: ex.Message);
        }
        catch (ConflictException ex)
        {
            return Problem(statusCode: 409, title: "Xung đột nghiệp vụ", detail: ex.Message);
        }
    }

    /// <summary>
    /// Hủy lệnh chuẩn bị (bắt buộc nêu rõ lý do hủy).
    /// </summary>
    [HttpPost("tasks/{id:guid}/cancel")]
    [Authorize(Roles = "Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<PreparationTaskDto>> CancelTask(
        Guid id,
        [FromBody] CancelTaskRequestDto dto,
        CancellationToken cancellationToken)
    {
        var valResult = await _cancelValidator.ValidateAsync(dto, cancellationToken);
        if (!valResult.IsValid)
        {
            return Problem(
                statusCode: 422,
                title: "Dữ liệu yêu cầu không hợp lệ",
                detail: string.Join("; ", valResult.Errors.Select(e => e.ErrorMessage)));
        }

        try
        {
            var task = await _service.CancelTaskAsync(id, dto, cancellationToken);
            return Ok(task);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy", detail: ex.Message);
        }
        catch (ConflictException ex)
        {
            return Problem(statusCode: 409, title: "Xung đột nghiệp vụ", detail: ex.Message);
        }
    }

    /// <summary>
    /// Ghi nhận tạm ngắt điện container lạnh để đảo chuyển/di chuyển (Dwell timer 30-45 phút).
    /// </summary>
    [HttpPost("tasks/{id:guid}/unplug-reefer")]
    [Authorize(Roles = "Dispatcher,YardStaff,Yard Staff,Yard Operator,Administrator,Admin")]
    public async Task<ActionResult<PreparationTaskDto>> UnplugReefer(
        Guid id,
        [FromQuery] int graceMinutes = 45,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await _service.HandleReeferUnplugAsync(id, graceMinutes, cancellationToken);
            return Ok(task);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy", detail: ex.Message);
        }
    }

    /// <summary>
    /// Xác nhận đã cắm lại điện cho container lạnh tại vị trí mới an toàn.
    /// </summary>
    [HttpPost("tasks/{id:guid}/replug-reefer")]
    [Authorize(Roles = "Dispatcher,YardStaff,Yard Staff,Yard Operator,Administrator,Admin")]
    public async Task<ActionResult<PreparationTaskDto>> ReplugReefer(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await _service.HandleReeferReplugAsync(id, cancellationToken);
            return Ok(task);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy", detail: ex.Message);
        }
    }

    /// <summary>
    /// Xác nhận sẵn sàng giao sau khi mọi WorkOrder hoàn thành (so khớp số container và số chì thực tế với e-D/O).
    /// </summary>
    [HttpPost("tasks/{id:guid}/confirm-ready")]
    [Authorize(Roles = "Dispatcher,YardStaff,Yard Staff,Yard Operator,Administrator,Admin")]
    public async Task<ActionResult<PreparationTaskDto>> ConfirmReady(
        Guid id,
        [FromBody] ConfirmReadyRequestDto dto,
        CancellationToken cancellationToken)
    {
        var valResult = await _confirmReadyValidator.ValidateAsync(dto, cancellationToken);
        if (!valResult.IsValid)
        {
            return Problem(
                statusCode: 422,
                title: "Dữ liệu yêu cầu không hợp lệ",
                detail: string.Join("; ", valResult.Errors.Select(e => e.ErrorMessage)));
        }

        try
        {
            var task = await _service.ConfirmReadyAsync(id, dto, cancellationToken);
            return Ok(task);
        }
        catch (NotFoundException ex)
        {
            return Problem(statusCode: 404, title: "Không tìm thấy", detail: ex.Message);
        }
        catch (ConflictException ex)
        {
            return Problem(statusCode: 409, title: "Xác nhận không thành công", detail: ex.Message);
        }
    }

    /// <summary>
    /// Lập kế hoạch theo lô danh sách lệnh trong ngày để tối ưu đảo chuyển.
    /// </summary>
    [HttpPost("plan-batch")]
    [Authorize(Roles = "Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<PlanBatchResultDto>> PlanBatch(
        [FromBody] PlanBatchRequestDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _service.PlanBatchAsync(dto, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Bảng điều khiển thống kê số lệnh chờ, nguy cơ trễ, đang làm, đã sẵn sàng trong ngày, tổng số lượt đảo chuyển.
    /// </summary>
    [HttpGet("dashboard")]
    [Authorize(Roles = "Dispatcher,YardStaff,Yard Staff,Yard Operator,Administrator,Admin")]
    public async Task<ActionResult<PreparationDashboardDto>> GetDashboard(CancellationToken cancellationToken)
    {
        var stats = await _service.GetDashboardAsync(cancellationToken);
        return Ok(stats);
    }

    /// <summary>
    /// Xem cấu hình chuẩn bị container.
    /// </summary>
    [HttpGet("config")]
    [Authorize(Roles = "Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<PreparationConfigDto>> GetConfig(CancellationToken cancellationToken)
    {
        var config = await _service.GetConfigAsync(cancellationToken);
        return Ok(config);
    }

    /// <summary>
    /// Cập nhật cấu hình chuẩn bị (Chỉ Quản trị viên).
    /// </summary>
    [HttpPut("config")]
    [Authorize(Roles = "Administrator,Admin")]
    public async Task<ActionResult<PreparationConfigDto>> UpdateConfig(
        [FromBody] PreparationConfigDto dto,
        CancellationToken cancellationToken)
    {
        var valResult = await _configValidator.ValidateAsync(dto, cancellationToken);
        if (!valResult.IsValid)
        {
            return Problem(
                statusCode: 422,
                title: "Cấu hình không hợp lệ",
                detail: string.Join("; ", valResult.Errors.Select(e => e.ErrorMessage)));
        }

        var config = await _service.UpdateConfigAsync(dto, cancellationToken);
        return Ok(config);
    }

    /// <summary>
    /// Kích hoạt kiểm tra và tạo lệnh chuẩn bị container tự động khi có sự kiện SlotConfirmed hoặc ContainerReleasable.
    /// </summary>
    [HttpPost("trigger-check")]
    [Authorize(Roles = "Dispatcher,Administrator,Admin")]
    public async Task<ActionResult<PreparationTaskDto>> TriggerCheck(
        [FromQuery] Guid bookingId,
        [FromQuery] string containerNo,
        [FromQuery] Guid? serviceOrderId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerNo))
        {
            return Problem(statusCode: 400, title: "Thiếu dữ liệu", detail: "Số container không được để trống.");
        }

        var task = await _service.CheckAndTriggerPreparationAsync(bookingId, containerNo, serviceOrderId, cancellationToken);
        if (task == null)
        {
            return Problem(
                statusCode: 422,
                title: "Chưa đủ điều kiện tạo lệnh",
                detail: $"Container {containerNo} chưa thỏa mãn cả 2 điều kiện: Khung giờ giao xác nhận và Sẵn sàng giải phóng (e-D/O hợp lệ, không bị giữ).");
        }

        return Ok(task);
    }
}

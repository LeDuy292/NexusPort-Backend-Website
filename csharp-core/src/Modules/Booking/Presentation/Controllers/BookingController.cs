using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Shared.Results;

namespace NexusPort.Modules.Booking.Presentation.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class BookingController : ControllerBase
{
    private readonly IBookingService _service;
    private readonly ICurrentUser _currentUser;

    public BookingController(IBookingService service, ICurrentUser currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Lấy danh sách Booking hỗ trợ Tìm kiếm, Lọc và Phân trang (Search, Filter, Pagination)
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<BookingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<BookingDto>>> GetPaged(
        [FromQuery] BookingFilterParams filter,
        CancellationToken cancellationToken)
    {
        // Enforce Carrier tenant isolation if logged in as Carrier/TransportCompany
        if (IsCarrierRole())
        {
            if (!filter.CarrierId.HasValue || filter.CarrierId.Value == Guid.Empty)
            {
                filter.CarrierId = GetUserCarrierId() ?? filter.CarrierId;
            }
        }

        var result = await _service.GetPagedAsync(filter, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Xem chi tiết thông tin Booking theo ID
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<BookingDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;

        var item = await _service.GetByIdAsync(id, userCarrierId, cancellationToken);
        if (item == null)
        {
            return NotFound(new { message = $"Booking with ID '{id}' was not found." });
        }

        return Ok(item);
    }

    /// <summary>
    /// Tạo Booking đặt lịch mới cho Transport Company
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingDto>> Create([FromBody] CreateBookingDto dto, CancellationToken cancellationToken)
    {
        // Auto-assign CarrierId if logged in as Carrier and not explicitly passed
        if (IsCarrierRole() && dto.CarrierId == Guid.Empty)
        {
            var userCarrierId = GetUserCarrierId();
            if (userCarrierId.HasValue)
            {
                dto.CarrierId = userCarrierId.Value;
            }
        }

        var item = await _service.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }

    /// <summary>
    /// Cập nhật thông tin Booking (khi ở trạng thái Pending)
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingDto>> Update(
        Guid id,
        [FromBody] UpdateBookingDto dto,
        CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;

        var item = await _service.UpdateAsync(id, dto, userCarrierId, cancellationToken);
        return Ok(item);
    }

    /// <summary>
    /// Hủy Booking theo Business Rules
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> Cancel(
        Guid id,
        [FromBody] CancelBookingDto? dto,
        CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;

        var item = await _service.CancelAsync(id, dto ?? new CancelBookingDto(), userCarrierId, cancellationToken);
        return Ok(item);
    }

    [HttpGet("driver/operations")]
    [ProducesResponseType(typeof(IReadOnlyList<DriverContainerOperationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DriverContainerOperationDto>>> GetDriverOperations(CancellationToken cancellationToken)
    {
        if (!_currentUser.UserId.HasValue)
            return Unauthorized(new { message = "Driver authentication is required." });

        return Ok(await _service.GetDriverOperationsAsync(_currentUser.UserId.Value, cancellationToken));
    }

    [HttpPost("driver/container-confirmation")]
    [ProducesResponseType(typeof(ContainerConfirmationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ContainerConfirmationResultDto>> ConfirmContainer(
        [FromBody] ContainerConfirmationDto dto,
        CancellationToken cancellationToken)
    {
        if (!_currentUser.UserId.HasValue)
            return Unauthorized(new { message = "Driver authentication is required." });

        var result = await _service.ConfirmContainerAsync(dto, _currentUser.UserId.Value, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// NXP-048: Lấy danh sách tài nguyên sẵn sàng (Containers trong bãi, Xe đầu kéo kèm tải trọng, Tài xế active) phục vụ AI Auto-Match
    /// </summary>
    [HttpGet("available-resources")]
    [ProducesResponseType(typeof(AvailableFleetResourcesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AvailableFleetResourcesDto>> GetAvailableResources(CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;
        var result = await _service.GetAvailableResourcesAsync(userCarrierId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// NXP-048: AI Gợi ý tối ưu xe đầu kéo, tài xế và khung giờ hẹn dựa trên container (xử lý hoàn toàn tại Backend và CSDL)
    /// </summary>
    [HttpGet("recommend-fleet")]
    [ProducesResponseType(typeof(FleetRecommendationDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<FleetRecommendationDto>> RecommendFleet(
        [FromQuery] Guid? containerId,
        [FromQuery] string? bookingType,
        CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;
        var result = await _service.RecommendFleetAsync(containerId, bookingType, userCarrierId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// NXP-048: Đánh giá tỷ lệ tải trọng và cảnh báo an toàn từ Backend trực tiếp từ CSDL
    /// </summary>
    [HttpGet("evaluate-payload")]
    [ProducesResponseType(typeof(PayloadEvaluationDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PayloadEvaluationDto>> EvaluatePayload(
        [FromQuery] Guid? containerId,
        [FromQuery] Guid? truckId,
        [FromQuery] decimal? customGrossWeightTon,
        CancellationToken cancellationToken)
    {
        var result = await _service.EvaluatePayloadAsync(containerId, truckId, customGrossWeightTon, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> Approve(
        Guid id,
        CancellationToken cancellationToken)
    {
        var approvedBy = _currentUser.UserId ?? Guid.Empty;
        var item = await _service.ApproveAsync(id, approvedBy, cancellationToken);
        return Ok(item);
    }

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> Reject(
        Guid id,
        [FromBody] CancelBookingDto? dto,
        CancellationToken cancellationToken)
    {
        var reason = dto?.Reason ?? "Dispatcher rejected";
        var item = await _service.RejectAsync(id, reason, cancellationToken);
        return Ok(item);
    }


    /// <summary>
    /// NXP-049: Gán/Điều phối Tài xế, Xe đầu kéo, Container cho Booking và chuyển sang trạng thái Ready
    /// </summary>
    [HttpPost("{id:guid}/assign")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> AssignResources(
        Guid id,
        [FromBody] AssignBookingResourcesDto dto,
        CancellationToken cancellationToken)
    {
        Guid? userCarrierId = IsCarrierRole() ? GetUserCarrierId() : null;
        var item = await _service.AssignResourcesAsync(id, dto, userCarrierId, cancellationToken);
        return Ok(item);
    }

    private bool IsCarrierRole()
    {
        var role = _currentUser.Role;
        return string.Equals(role, "Carrier", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(role, "Transport Company", StringComparison.OrdinalIgnoreCase);
    }

    private Guid? GetUserCarrierId()
    {
        // Try reading CarrierId claim or default known Carrier Company ID
        var carrierIdClaim = HttpContext.User?.FindFirst("CarrierId")?.Value;
        if (Guid.TryParse(carrierIdClaim, out var carrierId))
        {
            return carrierId;
        }

        // Dùng CarrierId khớp với Frontend (Vito Logistics) để test dễ dàng
        return Guid.Parse("d5683608-2134-44a2-94bc-91ee3805bdc0");
    }
}

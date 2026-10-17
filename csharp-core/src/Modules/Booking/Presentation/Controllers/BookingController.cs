using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Shared.Results;

namespace NexusPort.Modules.Booking.Presentation.Controllers;

[Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff,Driver")]
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class BookingController : ControllerBase
{
    private readonly IBookingService _service;
    private readonly ICurrentUser _currentUser;
    private readonly AppDbContext _context;

    public BookingController(IBookingService service, ICurrentUser currentUser, AppDbContext context)
    {
        _service = service;
        _currentUser = currentUser;
        _context = context;
    }

    /// <summary>
    /// Lấy danh sách Booking hỗ trợ Tìm kiếm, Lọc và Phân trang (Search, Filter, Pagination)
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<BookingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<BookingDto>>> GetPaged(
        [FromQuery] BookingFilterParams filter,
        CancellationToken cancellationToken)
    {
        // Enforce Carrier tenant isolation if logged in as Carrier/TransportCompany
        if (IsCarrierRole())
        {
            filter.CarrierId = GetUserCarrierId();
        }

        var result = await _service.GetPagedAsync(filter, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Xem chi tiết thông tin Booking theo ID
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
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
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPost]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("booking-create")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingDto>> Create([FromBody] CreateBookingDto dto, CancellationToken cancellationToken)
    {
        // Auto-assign CarrierId if logged in as Carrier and not explicitly passed
        if (IsCarrierRole())
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
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
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
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
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
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
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
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPost("recommend-selection")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("booking-create")]
    public async Task<ActionResult<FleetRecommendationDto>> RecommendSelection([FromBody] BookingSelectionRecommendationDto selection, CancellationToken cancellationToken)
    {
        if (!IsCarrierRole()) throw new UnauthorizedException("Chỉ công ty vận tải phân tích cho lần giao nhận của mình.");
        var carrierId = GetUserCarrierId();
        if (selection.BookingId.HasValue)
        {
            var booking = await _service.GetByIdAsync(selection.BookingId.Value, carrierId, cancellationToken);
            var link = booking?.ContainerSelections.SingleOrDefault(x => x.ContainerId == selection.ContainerId);
            if (link == null) throw new ValidationException("Container", "Container không thuộc booking đang điều phối.");
            // The booked private declaration is already scoped to this company.
            return Ok(await _service.RecommendFleetAsync(selection.ContainerId, "Dropoff", carrierId, cancellationToken, link.DeclarationId));
        }
        return Ok(await _service.RecommendFleetAsync(selection.ContainerId, selection.BookingType, carrierId, cancellationToken, selection.DeclarationId, selection.YardCode));
    }

    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
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
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
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

    [Authorize(Roles = "Administrator,Admin,Dispatcher")]
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

    [Authorize(Roles = "Administrator,Admin,Dispatcher")]
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
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
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

    /// <summary>
    /// Lấy chi tiết thông tin cước phí & thanh toán VietQR cho Booking
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpGet("{id:guid}/payment")]
    [ProducesResponseType(typeof(BookingPaymentInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingPaymentInfoDto>> GetPaymentInfo(
        Guid id,
        CancellationToken cancellationToken)
    {
        await _service.GetByIdAsync(id, IsCarrierRole() ? GetUserCarrierId() : null, cancellationToken);
        var result = await _service.GetPaymentInfoAsync(id, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Thực hiện / Xác nhận thanh toán cước cảng qua VietQR Napas 24/7
    /// </summary>
    [Authorize(Roles = "Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
    [HttpPost("{id:guid}/payment")]
    [ProducesResponseType(typeof(BookingPaymentInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingPaymentInfoDto>> ProcessPayment(
        Guid id,
        [FromBody] ProcessBookingPaymentDto dto,
        CancellationToken cancellationToken)
    {
        await _service.GetByIdAsync(id, IsCarrierRole() ? GetUserCarrierId() : null, cancellationToken);
        var result = await _service.ProcessPaymentAsync(id, dto, cancellationToken);
        return Ok(result);
    }

    private bool IsCarrierRole()
    {
        var role = _currentUser.Role;
        return string.Equals(role, "Carrier", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(role, "Transport Company", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(role, "Carrier Staff", StringComparison.OrdinalIgnoreCase);
    }

    private Guid? GetUserCarrierId()
    {
        if (!_currentUser.UserId.HasValue) throw new UnauthorizedException("Vui lòng đăng nhập.");
        var memberships = _context.Database.SqlQueryRaw<Guid>(
            "SELECT carrier_id AS \"Value\" FROM carrier_users WHERE user_id={0}", _currentUser.UserId.Value).ToList();
        if (memberships.Count != 1) throw new UnauthorizedException("Tài khoản cần thuộc đúng một công ty vận tải.");
        return memberships[0];
    }
}

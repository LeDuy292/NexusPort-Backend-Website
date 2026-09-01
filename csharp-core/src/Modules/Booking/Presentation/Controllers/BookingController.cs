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
        if (IsCarrierRole() && _currentUser.UserId.HasValue)
        {
            filter.CarrierId = GetUserCarrierId() ?? filter.CarrierId;
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

    private bool IsCarrierRole()
    {
        var role = _currentUser.Role;
        return string.Equals(role, "Carrier", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(role, "TransportCompany", StringComparison.OrdinalIgnoreCase);
    }

    private Guid? GetUserCarrierId()
    {
        // Try reading CarrierId claim or UserId
        var carrierIdClaim = HttpContext.User?.FindFirst("CarrierId")?.Value;
        if (Guid.TryParse(carrierIdClaim, out var carrierId))
        {
            return carrierId;
        }

        return _currentUser.UserId;
    }
}

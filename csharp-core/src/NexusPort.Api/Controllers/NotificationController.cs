using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Notifications.DTOs;
using NexusPort.Infrastructure.Notifications.Interfaces;
using NexusPort.Shared.Results;

namespace NexusPort.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class NotificationController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly ICurrentUser _currentUser;

    public NotificationController(INotificationService notificationService, ICurrentUser currentUser)
    {
        _notificationService = notificationService;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Lấy danh sách thông báo cá nhân của người dùng đang đăng nhập (Phân trang & Lọc chưa đọc)
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<NotificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<NotificationDto>>> GetUserNotifications(
        [FromQuery] NotificationFilterParams filter,
        CancellationToken cancellationToken)
    {
        var recipientId = GetCurrentUserId();
        if (recipientId == Guid.Empty)
        {
            return Unauthorized(new { message = "Authentication required to access notifications." });
        }

        var result = await _notificationService.GetUserNotificationsAsync(recipientId, filter, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Lấy số lượng thông báo chưa đọc của người dùng đang đăng nhập
    /// </summary>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<object>> GetUnreadCount(CancellationToken cancellationToken)
    {
        var recipientId = GetCurrentUserId();
        if (recipientId == Guid.Empty)
        {
            return Unauthorized(new { message = "Authentication required." });
        }

        var count = await _notificationService.GetUnreadCountAsync(recipientId, cancellationToken);
        return Ok(new { unreadCount = count });
    }

    /// <summary>
    /// Đánh dấu 1 thông báo là đã đọc
    /// </summary>
    [HttpPost("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkAsRead(Guid id, CancellationToken cancellationToken)
    {
        var recipientId = GetCurrentUserId();
        if (recipientId == Guid.Empty)
        {
            return Unauthorized(new { message = "Authentication required." });
        }

        var success = await _notificationService.MarkAsReadAsync(id, recipientId, cancellationToken);
        if (!success)
        {
            return NotFound(new { message = $"Notification '{id}' was not found or does not belong to you." });
        }

        return Ok(new { message = "Notification marked as read." });
    }

    /// <summary>
    /// Đánh dấu tất cả thông báo của người dùng là đã đọc
    /// </summary>
    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken cancellationToken)
    {
        var recipientId = GetCurrentUserId();
        if (recipientId == Guid.Empty)
        {
            return Unauthorized(new { message = "Authentication required." });
        }

        await _notificationService.MarkAllAsReadAsync(recipientId, cancellationToken);
        return Ok(new { message = "All notifications marked as read." });
    }

    /// <summary>
    /// Gửi thông báo mới đến người dùng (Dành cho Admin / System / Inter-module Trigger)
    /// </summary>
    [HttpPost("send")]
    [ProducesResponseType(typeof(NotificationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NotificationDto>> Send([FromBody] SendNotificationDto dto, CancellationToken cancellationToken)
    {
        if (dto.RecipientId == Guid.Empty)
        {
            return BadRequest(new { message = "RecipientId is required." });
        }

        if (string.IsNullOrWhiteSpace(dto.Title) || string.IsNullOrWhiteSpace(dto.Message))
        {
            return BadRequest(new { message = "Title and Message are required." });
        }

        var notification = await _notificationService.SendAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetUserNotifications), new { id = notification.Id }, notification);
    }

    private Guid GetCurrentUserId()
    {
        return _currentUser.UserId ?? Guid.Empty;
    }
}

using NexusPort.Infrastructure.Notifications.DTOs;
using NexusPort.Shared.Results;

namespace NexusPort.Infrastructure.Notifications.Interfaces;

public interface INotificationService
{
    Task<NotificationDto> SendAsync(SendNotificationDto dto, CancellationToken cancellationToken = default);
    Task<PagedResult<NotificationDto>> GetUserNotificationsAsync(Guid recipientId, NotificationFilterParams filter, CancellationToken cancellationToken = default);
    Task<int> GetUnreadCountAsync(Guid recipientId, CancellationToken cancellationToken = default);
    Task<bool> MarkAsReadAsync(Guid notificationId, Guid recipientId, CancellationToken cancellationToken = default);
    Task<bool> MarkAllAsReadAsync(Guid recipientId, CancellationToken cancellationToken = default);
}

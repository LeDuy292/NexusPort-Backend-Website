using NexusPort.Infrastructure.Notifications.Enums;

namespace NexusPort.Infrastructure.Notifications.DTOs;

public class NotificationDto
{
    public Guid Id { get; set; }
    public Guid RecipientId { get; set; }
    public NotificationType Type { get; set; }
    public NotificationSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? ReferenceId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SendNotificationDto
{
    public Guid RecipientId { get; set; }
    public NotificationType Type { get; set; } = NotificationType.SystemAlert;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ReferenceId { get; set; }
}

public class NotificationFilterParams
{
    public bool? UnreadOnly { get; set; }
    public NotificationType? Type { get; set; }
    public NotificationSeverity? Severity { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

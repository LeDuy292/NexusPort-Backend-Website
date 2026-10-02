using NexusPort.Infrastructure.Notifications.Enums;
using NexusPort.Shared.Kernel;

namespace NexusPort.Infrastructure.Notifications.Entities;

public class Notification : BaseEntity
{
    public Guid RecipientId { get; set; }
    public NotificationType Type { get; set; } = NotificationType.SystemAlert;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; } = false;
    public DateTime? ReadAt { get; set; }
    public string? ReferenceId { get; set; }

    public Notification() { }

    public Notification(
        Guid recipientId,
        NotificationType type,
        string title,
        string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        string? referenceId = null)
    {
        RecipientId = recipientId;
        Type = type;
        Title = title;
        Message = message;
        Severity = severity;
        ReferenceId = referenceId;
        IsRead = false;
    }

    public void MarkAsRead()
    {
        if (!IsRead)
        {
            IsRead = true;
            ReadAt = DateTime.UtcNow;
        }
    }
}

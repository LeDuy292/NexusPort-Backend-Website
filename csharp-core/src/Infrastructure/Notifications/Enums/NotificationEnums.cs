namespace NexusPort.Infrastructure.Notifications.Enums;

public enum NotificationType
{
    BookingApproved,
    BookingRejected,
    GatePassCreated,
    GateInFailed,
    ContainerReady,
    YardOperationCompleted,
    ContainerIssue,
    TrafficCongestion,
    GateOutFailed,
    SystemAlert
}

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Critical
}

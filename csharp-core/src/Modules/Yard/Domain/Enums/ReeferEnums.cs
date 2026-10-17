namespace NexusPort.Modules.Yard.Domain.Enums;

public enum SocketUsageStatus
{
    AVAILABLE,
    RESERVED,
    OCCUPIED
}

public enum SocketTechnicalStatus
{
    HEALTHY,
    FAULTY,
    UNDER_MAINTENANCE,
    DECOMMISSIONED
}

public enum PowerSessionStatus
{
    AWAITING_POWER,
    ACTIVE,
    SUSPENDED,
    COMPLETED,
    TRANSFERRED,
    CANCELLED
}

public enum ActualPowerStatus
{
    UNKNOWN,
    POWERED_ON,
    POWER_LOST,
    POWERED_OFF
}

public enum TempDataSource
{
    MANUAL_INSPECTION,
    IOT_TELEMATICS,
    PLC_GATEWAY
}

public enum AlertSeverity
{
    INFO,
    WARNING,
    CRITICAL
}

public enum ReeferAlertStatus
{
    TRIGGERED,
    ACKNOWLEDGED,
    IN_PROGRESS,
    RESOLVED,
    DISMISSED
}

public enum ReeferRequestStatus
{
    PENDING_ASSIGNMENT,
    ASSIGNED,
    IN_PROGRESS,
    COMPLETED,
    CANCELLED,
    REJECTED
}

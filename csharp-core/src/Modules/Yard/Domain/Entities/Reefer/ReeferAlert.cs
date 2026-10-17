using NexusPort.Modules.Yard.Domain.Enums;

namespace NexusPort.Modules.Yard.Domain.Entities.Reefer;

public class ReeferAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AlertCode { get; set; } = string.Empty;
    public Guid? SessionId { get; set; }
    public Guid? PlugId { get; set; }
    public Guid? ContainerId { get; set; }
    public string AlertType { get; set; } = "TEMPERATURE_EXCURSION";
    public AlertSeverity Severity { get; set; } = AlertSeverity.WARNING;
    public ReeferAlertStatus Status { get; set; } = ReeferAlertStatus.TRIGGERED;
    public string Message { get; set; } = string.Empty;
    public string? Payload { get; set; } // JSONB stored as string
    public Guid? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public ReeferPowerSession? Session { get; set; }
}

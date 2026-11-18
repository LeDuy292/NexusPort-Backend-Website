using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Preparation.Domain;

public class ExceptionItem : BaseEntity
{
    public string Category { get; set; } = "Preparation";
    public Guid? EntityId { get; set; }
    public string? ContainerNo { get; set; }
    public string Reason { get; set; } = string.Empty;
    public ExceptionSeverity Severity { get; set; } = ExceptionSeverity.Medium;
    public ExceptionStatus Status { get; set; } = ExceptionStatus.Open;
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Notes { get; set; }

    public ExceptionItem() { }

    public ExceptionItem(string category, string reason, ExceptionSeverity severity, Guid? entityId = null, string? containerNo = null, string? notes = null)
    {
        Id = Guid.NewGuid();
        Category = category;
        Reason = reason;
        Severity = severity;
        EntityId = entityId;
        ContainerNo = containerNo;
        Notes = notes;
        Status = ExceptionStatus.Open;
        CreatedAt = DateTime.UtcNow;
    }
}

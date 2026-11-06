using NexusPort.Modules.Yard.Domain.Enums;
using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities.Reefer;

public class ReeferPowerSession : BaseEntity, IAggregateRoot
{
    public Guid ContainerId { get; set; }
    public Guid PlugId { get; set; }
    public Guid? RequestId { get; set; }
    public PowerSessionStatus SessionStatus { get; set; } = PowerSessionStatus.AWAITING_POWER;
    public ActualPowerStatus ActualPowerStatus { get; set; } = ActualPowerStatus.UNKNOWN;
    public Guid? AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public Guid? PowerConfirmedBy { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastCheckedAt { get; set; }
    public string? Note { get; set; }
    public string? TerminationReason { get; set; }
    public Guid? ParentSessionId { get; set; }
    public decimal BillableHours { get; set; } = 0.00m;

    // Navigation properties
    public ReeferPlug Plug { get; set; } = null!;
    public ReeferPowerRequest? Request { get; set; }
    public ICollection<ReeferTemperatureRecord> TemperatureRecords { get; set; } = new List<ReeferTemperatureRecord>();
    public ICollection<ReeferAlert> Alerts { get; set; } = new List<ReeferAlert>();
}

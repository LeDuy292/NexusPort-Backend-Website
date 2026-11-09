using NexusPort.Modules.Yard.Domain.Enums;
using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities.Reefer;

public class ReeferPowerRequest : BaseEntity
{
    public string RequestNo { get; set; } = string.Empty;
    public Guid ContainerId { get; set; }
    public decimal TargetTemperature { get; set; } = -18.00m;
    public string TemperatureUnit { get; set; } = "C";
    public decimal TempTolerance { get; set; } = 2.00m;
    public string? CargoDescription { get; set; }
    public string? SpecialInstructions { get; set; }
    public ReeferRequestStatus Status { get; set; } = ReeferRequestStatus.PENDING_ASSIGNMENT;
    public Guid? RequestedBy { get; set; }
    public string? CancelledReason { get; set; }

    public ICollection<ReeferPowerSession> Sessions { get; set; } = new List<ReeferPowerSession>();
}

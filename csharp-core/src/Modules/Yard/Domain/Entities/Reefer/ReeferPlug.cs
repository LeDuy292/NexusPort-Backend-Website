using NexusPort.Modules.Yard.Domain.Enums;
using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities.Reefer;

public class ReeferPlug : BaseEntity
{
    public Guid? SlotId { get; set; }
    public string PlugCode { get; set; } = string.Empty;
    public bool IsAvailable { get; set; } = true;
    public bool IsPowerOn { get; set; } = false;
    public SocketUsageStatus UsageStatus { get; set; } = SocketUsageStatus.AVAILABLE;
    public SocketTechnicalStatus TechnicalStatus { get; set; } = SocketTechnicalStatus.HEALTHY;
    public string VoltageType { get; set; } = "380V-3Ph-32A";
    public decimal? MaxPowerKw { get; set; } = 15.00m;
    public DateTime? LastMaintainedAt { get; set; }

    public ICollection<ReeferPowerSession> PowerSessions { get; set; } = new List<ReeferPowerSession>();
}

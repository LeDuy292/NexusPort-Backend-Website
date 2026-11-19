using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Preparation.Domain;

public class PreparationConfig : BaseEntity
{
    public int LeadTimeMinutes { get; set; } = 90;
    public int MaxRehandlePerTask { get; set; } = 6;
    public Guid? StagingBlockId { get; set; }
    public int RescheduleGraceMinutes { get; set; } = 15;
    public int DelayCheckIntervalSeconds { get; set; } = 60;
    public int EstimatedMinutesPerMove { get; set; } = 8;
    public int WeightDueNear { get; set; } = 50;
    public int WeightReefer { get; set; } = 30;
    public int WeightExpiringFreeTime { get; set; } = 20;

    // Cấu hình nghiệp vụ Chiều Xuất (Outbound Vessel Loading)
    public int OutboundLeadTimeMinutes { get; set; } = 150;
    public int ReeferMaxUnplugMinutes { get; set; } = 45;
    public int ClosingTimeGraceMinutes { get; set; } = 30;
    public OutboundMarshallingMode MarshallingMode { get; set; } = OutboundMarshallingMode.InBayHousekeeping;

    public PreparationConfig()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        CreatedAt = DateTime.UtcNow;
    }
}

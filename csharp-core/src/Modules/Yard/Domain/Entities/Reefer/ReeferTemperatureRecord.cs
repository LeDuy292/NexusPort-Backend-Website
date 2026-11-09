using NexusPort.Modules.Yard.Domain.Enums;

namespace NexusPort.Modules.Yard.Domain.Entities.Reefer;

public class ReeferTemperatureRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public decimal? SupplyTemperature { get; set; }
    public decimal ReturnTemperature { get; set; }
    public string TemperatureUnit { get; set; } = "C";
    public decimal? AmbientTemperature { get; set; }
    public TempDataSource DataSource { get; set; } = TempDataSource.IOT_TELEMATICS;
    public bool IsAbnormal { get; set; } = false;
    public Guid? RecordedBy { get; set; }
    public string? DeviceId { get; set; }
    public string? Notes { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public ReeferPowerSession Session { get; set; } = null!;
}

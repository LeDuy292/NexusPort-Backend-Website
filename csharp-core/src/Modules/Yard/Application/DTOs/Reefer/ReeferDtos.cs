namespace NexusPort.Modules.Yard.Application.DTOs.Reefer;

public class ReeferMonitoringOverviewDto
{
    public int TotalPlugs { get; set; }
    public int AvailablePlugs { get; set; }
    public int OccupiedPlugs { get; set; }
    public int ReservedPlugs { get; set; }
    public int FaultyPlugs { get; set; }
    public int ActiveSessions { get; set; }
    public int AwaitingPowerSessions { get; set; }
    public int PendingRequests { get; set; }
    public int ActiveAlerts { get; set; }
    public List<ReeferMonitoredSessionDto> MonitoredSessions { get; set; } = new();
}

public class ReeferMonitoredSessionDto
{
    public Guid SessionId { get; set; }
    public Guid ContainerId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public Guid PlugId { get; set; }
    public string PlugCode { get; set; } = string.Empty;
    public string SessionStatus { get; set; } = string.Empty;
    public string ActualPowerStatus { get; set; } = string.Empty;
    public decimal TargetTemperature { get; set; }
    public decimal TempTolerance { get; set; }
    public decimal? LatestTemperature { get; set; }
    public decimal? LatestSupplyTemperature { get; set; }
    public bool? LatestIsAbnormal { get; set; }
    public DateTime? LastMeasuredAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public string DataSource { get; set; } = "IOT_TELEMATICS";
    public int OpenAlertCount { get; set; }
}

public class ReeferTemperatureRecordDto
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public decimal? SupplyTemperature { get; set; }
    public decimal ReturnTemperature { get; set; }
    public string TemperatureUnit { get; set; } = "C";
    public decimal? AmbientTemperature { get; set; }
    public string DataSource { get; set; } = string.Empty;
    public bool IsAbnormal { get; set; }
    public DateTime RecordedAt { get; set; }
    public string? DeviceId { get; set; }
    public string? Notes { get; set; }
    public string PowerStatus { get; set; } = "POWERED_ON";
    public string? AlertMessage { get; set; }
    public string? AlertType { get; set; }
    public string? AlertSeverity { get; set; }
}

public class ReeferAlertDto
{
    public Guid Id { get; set; }
    public string AlertCode { get; set; } = string.Empty;
    public Guid? SessionId { get; set; }
    public Guid? PlugId { get; set; }
    public string? PlugCode { get; set; }
    public Guid? ContainerId { get; set; }
    public string? ContainerNo { get; set; }
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Payload { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNotes { get; set; }
}

public class RecordSimulatedTelemetryDto
{
    public decimal ReturnTemperature { get; set; }
    public decimal? SupplyTemperature { get; set; }
    public decimal? AmbientTemperature { get; set; }
    public string DataSource { get; set; } = "IOT_TELEMATICS";
    public string? DeviceId { get; set; }
    public string? Notes { get; set; }
    public string? SimulatedPowerStatus { get; set; } // POWERED_ON, POWER_LOST
}

public class AcknowledgeAlertDto
{
    public string? Notes { get; set; }
}

public class SimulateScenarioRequest
{
    public string Scenario { get; set; } = "NORMAL"; // NORMAL, EXCURSION, POWER_LOSS, RECOVERY
    public Guid? SessionId { get; set; }
    public decimal? CustomTemperature { get; set; }
}

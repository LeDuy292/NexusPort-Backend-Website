using NexusPort.Modules.Yard.Application.DTOs.Reefer;

namespace NexusPort.Modules.Yard.Application.Interfaces;

public interface IReeferMonitoringService
{
    Task<ReeferMonitoringOverviewDto> GetMonitoringOverviewAsync(CancellationToken cancellationToken = default);
    Task<List<ReeferTemperatureRecordDto>> GetSessionTemperatureHistoryAsync(Guid sessionId, int limit = 50, CancellationToken cancellationToken = default);
    Task<List<ReeferAlertDto>> GetAlertsAsync(string? status = null, string? severity = null, CancellationToken cancellationToken = default);
    Task<ReeferAlertDto?> AcknowledgeAlertAsync(Guid alertId, Guid userId, string? notes = null, CancellationToken cancellationToken = default);
    Task<ReeferAlertDto?> ResolveAlertAsync(Guid alertId, Guid userId, string notes, CancellationToken cancellationToken = default);
    Task<ReeferTemperatureRecordDto> RecordSimulatedTelemetryAsync(Guid sessionId, RecordSimulatedTelemetryDto dto, Guid? userId = null, CancellationToken cancellationToken = default);
    Task RunSimulationCycleAsync(CancellationToken cancellationToken = default);
    Task SetDemoScenarioAsync(SimulateScenarioRequest request, CancellationToken cancellationToken = default);
}

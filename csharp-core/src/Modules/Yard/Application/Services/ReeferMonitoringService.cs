using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Container.Domain.Entities;
using NexusPort.Modules.Yard.Application.DTOs.Reefer;
using NexusPort.Modules.Yard.Application.Interfaces;
using NexusPort.Modules.Yard.Domain.Entities.Reefer;
using NexusPort.Modules.Yard.Domain.Enums;

namespace NexusPort.Modules.Yard.Application.Services;

public class ReeferMonitoringService : IReeferMonitoringService
{
    private readonly AppDbContext _context;
    private readonly IHubContext<NexusPort.Modules.Yard.Infrastructure.Hubs.ReeferMonitoringHub, IReeferMonitoringHubClient> _hubContext;
    private readonly ILogger<ReeferMonitoringService> _logger;

    // In-memory demo state for scenario overrides per session or global
    private static readonly Dictionary<Guid, (string Scenario, decimal? CustomTemp)> _sessionScenarios = new();
    private static string _globalScenario = "NORMAL";
    private static int _simulationCycleCounter = 0;
    private static readonly SemaphoreSlim _simulationLock = new(1, 1);

    public ReeferMonitoringService(
        AppDbContext context,
        IHubContext<NexusPort.Modules.Yard.Infrastructure.Hubs.ReeferMonitoringHub, IReeferMonitoringHubClient> hubContext,
        ILogger<ReeferMonitoringService> logger)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<ReeferMonitoringOverviewDto> GetMonitoringOverviewAsync(CancellationToken cancellationToken = default)
    {
        var plugs = await _context.Set<ReeferPlug>().AsNoTracking().ToListAsync(cancellationToken);
        var sessions = await _context.Set<ReeferPowerSession>()
            .AsNoTracking()
            .Include(s => s.Plug)
            .Include(s => s.Request)
            .Where(s => s.SessionStatus == PowerSessionStatus.AWAITING_POWER || s.SessionStatus == PowerSessionStatus.ACTIVE)
            .ToListAsync(cancellationToken);

        var requests = await _context.Set<ReeferPowerRequest>()
            .AsNoTracking()
            .Where(r => r.Status == ReeferRequestStatus.PENDING_ASSIGNMENT)
            .CountAsync(cancellationToken);

        var activeAlerts = await _context.Set<ReeferAlert>()
            .AsNoTracking()
            .Where(a => a.Status == ReeferAlertStatus.TRIGGERED || a.Status == ReeferAlertStatus.ACKNOWLEDGED || a.Status == ReeferAlertStatus.IN_PROGRESS)
            .CountAsync(cancellationToken);

        var containerIds = sessions.Select(s => s.ContainerId).Distinct().ToList();
        var containers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .AsNoTracking()
            .Where(c => containerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ContainerNumber })
            .ToDictionaryAsync(c => c.Id, c => c.ContainerNumber, cancellationToken);

        var monitoredList = new List<ReeferMonitoredSessionDto>();

        foreach (var s in sessions)
        {
            var latestRecord = await _context.Set<ReeferTemperatureRecord>()
                .AsNoTracking()
                .Where(r => r.SessionId == s.Id)
                .OrderByDescending(r => r.RecordedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var sessionAlertsCount = await _context.Set<ReeferAlert>()
                .AsNoTracking()
                .Where(a => a.SessionId == s.Id && (a.Status == ReeferAlertStatus.TRIGGERED || a.Status == ReeferAlertStatus.ACKNOWLEDGED || a.Status == ReeferAlertStatus.IN_PROGRESS))
                .CountAsync(cancellationToken);

            containers.TryGetValue(s.ContainerId, out var containerNo);

            monitoredList.Add(new ReeferMonitoredSessionDto
            {
                SessionId = s.Id,
                ContainerId = s.ContainerId,
                ContainerNo = containerNo ?? "N/A",
                PlugId = s.PlugId,
                PlugCode = s.Plug?.PlugCode ?? "N/A",
                SessionStatus = s.SessionStatus.ToString(),
                ActualPowerStatus = s.ActualPowerStatus.ToString(),
                TargetTemperature = s.Request?.TargetTemperature ?? -18.00m,
                TempTolerance = s.Request?.TempTolerance ?? 2.00m,
                LatestTemperature = latestRecord?.ReturnTemperature,
                LatestSupplyTemperature = latestRecord?.SupplyTemperature,
                LatestIsAbnormal = latestRecord?.IsAbnormal,
                LastMeasuredAt = latestRecord?.RecordedAt ?? s.LastCheckedAt,
                StartedAt = s.StartedAt,
                DataSource = latestRecord != null ? latestRecord.DataSource.ToString() : "IOT_TELEMATICS",
                OpenAlertCount = sessionAlertsCount
            });
        }

        return new ReeferMonitoringOverviewDto
        {
            TotalPlugs = plugs.Count,
            AvailablePlugs = plugs.Count(p => p.UsageStatus == SocketUsageStatus.AVAILABLE && p.TechnicalStatus == SocketTechnicalStatus.HEALTHY),
            OccupiedPlugs = plugs.Count(p => p.UsageStatus == SocketUsageStatus.OCCUPIED),
            ReservedPlugs = plugs.Count(p => p.UsageStatus == SocketUsageStatus.RESERVED),
            FaultyPlugs = plugs.Count(p => p.TechnicalStatus != SocketTechnicalStatus.HEALTHY),
            ActiveSessions = sessions.Count(s => s.SessionStatus == PowerSessionStatus.ACTIVE),
            AwaitingPowerSessions = sessions.Count(s => s.SessionStatus == PowerSessionStatus.AWAITING_POWER),
            PendingRequests = requests,
            ActiveAlerts = activeAlerts,
            MonitoredSessions = monitoredList
        };
    }

    public async Task<List<ReeferTemperatureRecordDto>> GetSessionTemperatureHistoryAsync(
        Guid sessionId,
        int limit = 60,
        CancellationToken cancellationToken = default)
    {
        var records = await _context.Set<ReeferTemperatureRecord>()
            .AsNoTracking()
            .Where(r => r.SessionId == sessionId)
            .OrderByDescending(r => r.RecordedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var sessionAlerts = await _context.Set<ReeferAlert>()
            .AsNoTracking()
            .Where(a => a.SessionId == sessionId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        return records.Select(r =>
        {
            bool isPowerLost = (r.Notes != null && (r.Notes.Contains("POWER_LOSS") || r.Notes.Contains("POWER_LOST") || r.Notes.Contains("Mất điện"))) ||
                sessionAlerts.Any(a => a.AlertType == "SIMULATED_POWER_LOSS" &&
                                       a.CreatedAt <= r.RecordedAt.AddSeconds(30) &&
                                       (a.ResolvedAt == null || r.RecordedAt <= a.ResolvedAt.Value.AddSeconds(5)));

            var matchingAlert = sessionAlerts.FirstOrDefault(a => Math.Abs((a.CreatedAt - r.RecordedAt).TotalSeconds) <= 60)
                ?? (isPowerLost ? sessionAlerts.FirstOrDefault(a => a.AlertType == "SIMULATED_POWER_LOSS") : null)
                ?? (r.IsAbnormal ? sessionAlerts.FirstOrDefault(a => a.AlertType == "TEMPERATURE_EXCURSION") : null);

            return new ReeferTemperatureRecordDto
            {
                Id = r.Id,
                SessionId = r.SessionId,
                SupplyTemperature = r.SupplyTemperature,
                ReturnTemperature = r.ReturnTemperature,
                TemperatureUnit = r.TemperatureUnit,
                AmbientTemperature = r.AmbientTemperature,
                DataSource = r.DataSource.ToString(),
                IsAbnormal = r.IsAbnormal,
                RecordedAt = r.RecordedAt,
                DeviceId = r.DeviceId,
                Notes = r.Notes,
                PowerStatus = isPowerLost ? "POWER_LOST" : "POWERED_ON",
                AlertMessage = matchingAlert?.Message,
                AlertType = matchingAlert?.AlertType,
                AlertSeverity = matchingAlert?.Severity.ToString()
            };
        }).OrderBy(r => r.RecordedAt).ToList();
    }

    public async Task<List<ReeferAlertDto>> GetAlertsAsync(
        string? status = null,
        string? severity = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Set<ReeferAlert>()
            .AsNoTracking()
            .Include(a => a.Session)
            .ThenInclude(s => s!.Plug)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && status != "ALL" && Enum.TryParse<ReeferAlertStatus>(status, true, out var stEnum))
        {
            query = query.Where(a => a.Status == stEnum);
        }

        if (!string.IsNullOrWhiteSpace(severity) && severity != "ALL" && Enum.TryParse<AlertSeverity>(severity, true, out var sevEnum))
        {
            query = query.Where(a => a.Severity == sevEnum);
        }

        var alerts = await query.OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync(cancellationToken);
        var alertContainerIds = alerts.Where(a => a.ContainerId.HasValue).Select(a => a.ContainerId!.Value).Distinct().ToList();
        var containers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .AsNoTracking()
            .Where(c => alertContainerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ContainerNumber })
            .ToDictionaryAsync(c => c.Id, c => c.ContainerNumber, cancellationToken);

        return alerts.Select(a =>
        {
            string? contNo = null;
            if (a.ContainerId.HasValue && containers.TryGetValue(a.ContainerId.Value, out var cNum))
            {
                contNo = cNum;
            }

            return new ReeferAlertDto
            {
                Id = a.Id,
                AlertCode = a.AlertCode,
                SessionId = a.SessionId,
                PlugId = a.PlugId,
                PlugCode = a.Session?.Plug?.PlugCode,
                ContainerId = a.ContainerId,
                ContainerNo = contNo,
                AlertType = a.AlertType,
                Severity = a.Severity.ToString(),
                Status = a.Status.ToString(),
                Message = a.Message,
                Payload = a.Payload,
                CreatedAt = a.CreatedAt,
                AcknowledgedAt = a.AcknowledgedAt,
                ResolvedAt = a.ResolvedAt,
                ResolutionNotes = a.ResolutionNotes
            };
        }).ToList();
    }

    public async Task<ReeferAlertDto?> AcknowledgeAlertAsync(
        Guid alertId,
        Guid userId,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var alert = await _context.Set<ReeferAlert>().FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert == null) return null;

        alert.Status = ReeferAlertStatus.ACKNOWLEDGED;
        alert.AcknowledgedBy = userId;
        alert.AcknowledgedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            alert.ResolutionNotes = notes;
        }
        alert.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        // SignalR update event
        try
        {
            await _hubContext.Clients.Group("overview").ReeferAlertUpdated(new
            {
                id = alert.Id,
                status = alert.Status.ToString(),
                acknowledgedAt = alert.AcknowledgedAt,
                notes = alert.ResolutionNotes
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish SignalR alert updated event for alert {AlertId}", alertId);
        }

        return new ReeferAlertDto
        {
            Id = alert.Id,
            AlertCode = alert.AlertCode,
            SessionId = alert.SessionId,
            PlugId = alert.PlugId,
            AlertType = alert.AlertType,
            Severity = alert.Severity.ToString(),
            Status = alert.Status.ToString(),
            Message = alert.Message,
            Payload = alert.Payload,
            CreatedAt = alert.CreatedAt,
            AcknowledgedAt = alert.AcknowledgedAt,
            ResolutionNotes = alert.ResolutionNotes
        };
    }

    public async Task<ReeferAlertDto?> ResolveAlertAsync(
        Guid alertId,
        Guid userId,
        string notes,
        CancellationToken cancellationToken = default)
    {
        var alert = await _context.Set<ReeferAlert>().FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert == null) return null;

        alert.Status = ReeferAlertStatus.RESOLVED;
        alert.ResolvedBy = userId;
        alert.ResolvedAt = DateTime.UtcNow;
        alert.ResolutionNotes = notes;
        alert.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        // SignalR update event
        try
        {
            await _hubContext.Clients.Group("overview").ReeferAlertUpdated(new
            {
                id = alert.Id,
                status = alert.Status.ToString(),
                resolvedAt = alert.ResolvedAt,
                resolutionNotes = alert.ResolutionNotes
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish SignalR alert resolved event for alert {AlertId}", alertId);
        }

        return new ReeferAlertDto
        {
            Id = alert.Id,
            AlertCode = alert.AlertCode,
            SessionId = alert.SessionId,
            PlugId = alert.PlugId,
            AlertType = alert.AlertType,
            Severity = alert.Severity.ToString(),
            Status = alert.Status.ToString(),
            Message = alert.Message,
            Payload = alert.Payload,
            CreatedAt = alert.CreatedAt,
            ResolvedAt = alert.ResolvedAt,
            ResolutionNotes = alert.ResolutionNotes
        };
    }

    public async Task<ReeferTemperatureRecordDto> RecordSimulatedTelemetryAsync(
        Guid sessionId,
        RecordSimulatedTelemetryDto dto,
        Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await _context.Set<ReeferPowerSession>()
            .Include(s => s.Plug)
            .Include(s => s.Request)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session == null)
            throw new KeyNotFoundException($"Không tìm thấy phiên cấp điện với mã: {sessionId}");

        if (session.SessionStatus != PowerSessionStatus.ACTIVE)
            throw new InvalidOperationException($"Phiên cấp điện {sessionId} không ở trạng thái ACTIVE để ghi nhận đo đạc.");

        // Target temp & tolerance
        decimal targetTemp = session.Request?.TargetTemperature ?? -18.00m;
        decimal tolerance = session.Request?.TempTolerance ?? 2.00m;
        decimal minSafe = targetTemp - tolerance;
        decimal maxSafe = targetTemp + tolerance;

        bool isOutOfRange = dto.ReturnTemperature < minSafe || dto.ReturnTemperature > maxSafe;

        TempDataSource dataSource = TempDataSource.IOT_TELEMATICS;
        if (!string.IsNullOrWhiteSpace(dto.DataSource) && Enum.TryParse<TempDataSource>(dto.DataSource, true, out var parsedDs))
        {
            dataSource = parsedDs;
        }

        // Create temperature record (Immutable history)
        var record = new ReeferTemperatureRecord
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            SupplyTemperature = dto.SupplyTemperature,
            ReturnTemperature = dto.ReturnTemperature,
            TemperatureUnit = "C",
            AmbientTemperature = dto.AmbientTemperature,
            DataSource = dataSource,
            IsAbnormal = isOutOfRange,
            RecordedBy = userId,
            DeviceId = dto.DeviceId ?? "SIMULATOR_PLC_01",
            Notes = dto.Notes,
            RecordedAt = DateTime.UtcNow
        };

        _context.Set<ReeferTemperatureRecord>().Add(record);

        // Update session last checked
        session.LastCheckedAt = record.RecordedAt;

        if (!string.IsNullOrWhiteSpace(dto.SimulatedPowerStatus) && Enum.TryParse<ActualPowerStatus>(dto.SimulatedPowerStatus, true, out var parsedPower))
        {
            session.ActualPowerStatus = parsedPower;
            if (session.Plug != null)
            {
                session.Plug.IsPowerOn = (parsedPower == ActualPowerStatus.POWERED_ON);
            }
        }

        // Alert Evaluation with Anti-Flapping
        await EvaluateAlertsAsync(session, record, minSafe, maxSafe, targetTemp, tolerance, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        // Get container number for realtime event
        var container = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == session.ContainerId, cancellationToken);

        var openAlertsCount = await _context.Set<ReeferAlert>()
            .AsNoTracking()
            .Where(a => a.SessionId == sessionId && (a.Status == ReeferAlertStatus.TRIGGERED || a.Status == ReeferAlertStatus.ACKNOWLEDGED || a.Status == ReeferAlertStatus.IN_PROGRESS))
            .CountAsync(cancellationToken);

        // Publish SignalR MonitoringUpdated event
        try
        {
            var realtimePayload = new
            {
                sessionId = session.Id,
                containerId = session.ContainerId,
                containerNo = container?.ContainerNumber ?? "N/A",
                plugId = session.PlugId,
                plugCode = session.Plug?.PlugCode ?? "N/A",
                returnTemperature = record.ReturnTemperature,
                supplyTemperature = record.SupplyTemperature,
                targetTemperature = targetTemp,
                tempTolerance = tolerance,
                isAbnormal = record.IsAbnormal,
                actualPowerStatus = session.ActualPowerStatus.ToString(),
                dataSource = record.DataSource.ToString(),
                recordedAt = record.RecordedAt,
                openAlertCount = openAlertsCount
            };

            await _hubContext.Clients.Group("overview").MonitoringUpdated(realtimePayload);
            await _hubContext.Clients.Group($"session_{session.Id.ToString().ToLowerInvariant()}").MonitoringUpdated(realtimePayload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR dispatch failed for session {SessionId}. Persisted reading was preserved.", sessionId);
        }

        return new ReeferTemperatureRecordDto
        {
            Id = record.Id,
            SessionId = record.SessionId,
            SupplyTemperature = record.SupplyTemperature,
            ReturnTemperature = record.ReturnTemperature,
            TemperatureUnit = record.TemperatureUnit,
            AmbientTemperature = record.AmbientTemperature,
            DataSource = record.DataSource.ToString(),
            IsAbnormal = record.IsAbnormal,
            RecordedAt = record.RecordedAt,
            DeviceId = record.DeviceId,
            Notes = record.Notes
        };
    }

    private async Task EvaluateAlertsAsync(
        ReeferPowerSession session,
        ReeferTemperatureRecord currentRecord,
        decimal minSafe,
        decimal maxSafe,
        decimal targetTemp,
        decimal tolerance,
        CancellationToken cancellationToken)
    {
        // 1. Check Simulated Power Loss
        if (session.ActualPowerStatus == ActualPowerStatus.POWER_LOST)
        {
            var existingPowerAlert = await _context.Set<ReeferAlert>()
                .FirstOrDefaultAsync(a => a.SessionId == session.Id &&
                                          a.AlertType == "SIMULATED_POWER_LOSS" &&
                                          (a.Status == ReeferAlertStatus.TRIGGERED || a.Status == ReeferAlertStatus.ACKNOWLEDGED || a.Status == ReeferAlertStatus.IN_PROGRESS),
                                          cancellationToken);

            if (existingPowerAlert == null)
            {
                var powerAlert = new ReeferAlert
                {
                    Id = Guid.NewGuid(),
                    AlertCode = $"PWR_LOSS_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    SessionId = session.Id,
                    PlugId = session.PlugId,
                    ContainerId = session.ContainerId,
                    AlertType = "SIMULATED_POWER_LOSS",
                    Severity = AlertSeverity.CRITICAL,
                    Status = ReeferAlertStatus.TRIGGERED,
                    Message = $"Mất điện mô phỏng tại ổ cắm {session.Plug?.PlugCode ?? session.PlugId.ToString()}. Yêu cầu kiểm tra an toàn điện khẩn cấp.",
                    Payload = JsonSerializer.Serialize(new { actualPowerStatus = "POWER_LOST", detectedAt = DateTime.UtcNow }),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.Set<ReeferAlert>().Add(powerAlert);

                try
                {
                    await _hubContext.Clients.Group("overview").ReeferAlertCreated(new
                    {
                        id = powerAlert.Id,
                        alertCode = powerAlert.AlertCode,
                        sessionId = powerAlert.SessionId,
                        plugId = powerAlert.PlugId,
                        plugCode = session.Plug?.PlugCode,
                        containerId = powerAlert.ContainerId,
                        alertType = powerAlert.AlertType,
                        severity = powerAlert.Severity.ToString(),
                        status = powerAlert.Status.ToString(),
                        message = powerAlert.Message,
                        createdAt = powerAlert.CreatedAt
                    });
                }
                catch { }
            }
        }
        else if (session.ActualPowerStatus == ActualPowerStatus.POWERED_ON)
        {
            // Auto-resolve any active power loss alerts
            var openPowerAlerts = await _context.Set<ReeferAlert>()
                .Where(a => a.SessionId == session.Id &&
                            a.AlertType == "SIMULATED_POWER_LOSS" &&
                            (a.Status == ReeferAlertStatus.TRIGGERED || a.Status == ReeferAlertStatus.ACKNOWLEDGED || a.Status == ReeferAlertStatus.IN_PROGRESS))
                .ToListAsync(cancellationToken);

            foreach (var pa in openPowerAlerts)
            {
                pa.Status = ReeferAlertStatus.RESOLVED;
                pa.ResolvedAt = DateTime.UtcNow;
                pa.ResolutionNotes = "Nguồn điện đã được cấp lại bình thường.";
                pa.UpdatedAt = DateTime.UtcNow;

                try
                {
                    await _hubContext.Clients.Group("overview").ReeferAlertUpdated(new
                    {
                        id = pa.Id,
                        status = pa.Status.ToString(),
                        resolvedAt = pa.ResolvedAt,
                        resolutionNotes = pa.ResolutionNotes
                    });
                }
                catch { }
            }
        }

        // 2. Check Temperature Excursion with Anti-Flapping
        if (currentRecord.IsAbnormal)
        {
            decimal diffFromTarget = Math.Abs(currentRecord.ReturnTemperature - targetTemp);
            bool isCriticalExcursion = diffFromTarget >= 5.00m;

            // Check previous reading for anti-flapping
            var previousRecord = await _context.Set<ReeferTemperatureRecord>()
                .AsNoTracking()
                .Where(r => r.SessionId == session.Id && r.Id != currentRecord.Id)
                .OrderByDescending(r => r.RecordedAt)
                .FirstOrDefaultAsync(cancellationToken);

            bool shouldTrigger = isCriticalExcursion || (previousRecord != null && previousRecord.IsAbnormal);

            if (shouldTrigger)
            {
                var existingAlert = await _context.Set<ReeferAlert>()
                    .FirstOrDefaultAsync(a => a.SessionId == session.Id &&
                                              a.AlertType == "TEMPERATURE_EXCURSION" &&
                                              (a.Status == ReeferAlertStatus.TRIGGERED || a.Status == ReeferAlertStatus.ACKNOWLEDGED || a.Status == ReeferAlertStatus.IN_PROGRESS),
                                              cancellationToken);

                if (existingAlert == null)
                {
                    var newAlert = new ReeferAlert
                    {
                        Id = Guid.NewGuid(),
                        AlertCode = $"TEMP_EXCURSION_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        SessionId = session.Id,
                        PlugId = session.PlugId,
                        ContainerId = session.ContainerId,
                        AlertType = "TEMPERATURE_EXCURSION",
                        Severity = isCriticalExcursion ? AlertSeverity.CRITICAL : AlertSeverity.WARNING,
                        Status = ReeferAlertStatus.TRIGGERED,
                        Message = $"Nhiệt độ {currentRecord.ReturnTemperature:F1}°C vượt ngoài vùng an toàn [{minSafe:F1}°C đến {maxSafe:F1}°C] liên tiếp (Cài đặt: {targetTemp:F1}°C ± {tolerance:F1}°C)",
                        Payload = JsonSerializer.Serialize(new
                        {
                            targetTemp,
                            tolerance,
                            recordedTemp = currentRecord.ReturnTemperature,
                            safeRange = new[] { minSafe.ToString("F1"), maxSafe.ToString("F1") },
                            isCritical = isCriticalExcursion,
                            recordedAt = currentRecord.RecordedAt
                        }),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _context.Set<ReeferAlert>().Add(newAlert);

                    try
                    {
                        await _hubContext.Clients.Group("overview").ReeferAlertCreated(new
                        {
                            id = newAlert.Id,
                            alertCode = newAlert.AlertCode,
                            sessionId = newAlert.SessionId,
                            plugId = newAlert.PlugId,
                            plugCode = session.Plug?.PlugCode,
                            containerId = newAlert.ContainerId,
                            alertType = newAlert.AlertType,
                            severity = newAlert.Severity.ToString(),
                            status = newAlert.Status.ToString(),
                            message = newAlert.Message,
                            createdAt = newAlert.CreatedAt
                        });
                    }
                    catch { }
                }
                else
                {
                    // Update existing alert timestamp & message
                    existingAlert.UpdatedAt = DateTime.UtcNow;
                    existingAlert.Message = $"Nhiệt độ hiện tại {currentRecord.ReturnTemperature:F1}°C vẫn tiếp tục nằm ngoài vùng an toàn [{minSafe:F1}°C đến {maxSafe:F1}°C].";
                }
            }
        }
        else
        {
            // Auto-resolve temperature excursion when reading returns to safe range!
            var openTempAlerts = await _context.Set<ReeferAlert>()
                .Where(a => a.SessionId == session.Id &&
                            a.AlertType == "TEMPERATURE_EXCURSION" &&
                            (a.Status == ReeferAlertStatus.TRIGGERED || a.Status == ReeferAlertStatus.ACKNOWLEDGED || a.Status == ReeferAlertStatus.IN_PROGRESS))
                .ToListAsync(cancellationToken);

            foreach (var a in openTempAlerts)
            {
                a.Status = ReeferAlertStatus.RESOLVED;
                a.ResolvedAt = DateTime.UtcNow;
                a.ResolutionNotes = "Nhiệt độ đã trở lại dải an toàn đối xứng theo chu kỳ đo mới nhất.";
                a.UpdatedAt = DateTime.UtcNow;

                try
                {
                    await _hubContext.Clients.Group("overview").ReeferAlertUpdated(new
                    {
                        id = a.Id,
                        status = a.Status.ToString(),
                        resolvedAt = a.ResolvedAt,
                        resolutionNotes = a.ResolutionNotes
                    });
                }
                catch { }
            }
        }
    }

    public async Task RunSimulationCycleAsync(CancellationToken cancellationToken = default)
    {
        if (!await _simulationLock.WaitAsync(0, cancellationToken))
        {
            // Already running
            return;
        }

        try
        {
            var activeSessions = await _context.Set<ReeferPowerSession>()
                .Include(s => s.Plug)
                .Include(s => s.Request)
                .Where(s => s.SessionStatus == PowerSessionStatus.ACTIVE && s.ActualPowerStatus == ActualPowerStatus.POWERED_ON)
                .ToListAsync(cancellationToken);

            if (!activeSessions.Any())
            {
                // Auto-reactivate candidate sessions if none are active so telemetry is continuous
                var candidateSessions = await _context.Set<ReeferPowerSession>()
                    .Include(s => s.Plug)
                    .Include(s => s.Request)
                    .Take(2)
                    .ToListAsync(cancellationToken);

                foreach (var s in candidateSessions)
                {
                    s.SessionStatus = PowerSessionStatus.ACTIVE;
                    s.ActualPowerStatus = ActualPowerStatus.POWERED_ON;
                    s.StartedAt ??= DateTime.UtcNow;
                    if (s.Plug != null)
                    {
                        s.Plug.UsageStatus = SocketUsageStatus.OCCUPIED;
                    }
                }

                if (candidateSessions.Any())
                {
                    await _context.SaveChangesAsync(cancellationToken);
                    activeSessions = candidateSessions;
                }
                else
                {
                    return;
                }
            }

            _simulationCycleCounter++;
            var random = new Random();

            for (int i = 0; i < activeSessions.Count; i++)
            {
                var session = activeSessions[i];

                // Determine scenario
                string scenario = _globalScenario;
                decimal? customTemp = null;
                if (_sessionScenarios.TryGetValue(session.Id, out var sessionScen))
                {
                    scenario = sessionScen.Scenario;
                    customTemp = sessionScen.CustomTemp;
                }

                decimal targetTemp = session.Request?.TargetTemperature ?? -18.00m;
                decimal tolerance = session.Request?.TempTolerance ?? 2.00m;
                decimal returnTemp;
                string? powerStatus = null;

                // Autonomous realistic operational cycle (when no manual scenario override is set)
                if (scenario == "NORMAL" && !customTemp.HasValue)
                {
                    // Period of 14 cycles (~84 seconds)
                    // Every ~84 seconds, container 0 undergoes a realistic temperature excursion that triggers an alert and self-stabilizes
                    int cycleInPeriod = _simulationCycleCounter % 14;

                    if (i == 0 && cycleInPeriod >= 7 && cycleInPeriod <= 10)
                    {
                        if (cycleInPeriod == 7)
                        {
                            // Cycle 7: Temperature begins drifting above tolerance
                            returnTemp = targetTemp + tolerance + 0.9m + (decimal)(random.NextDouble() * 0.3);
                        }
                        else if (cycleInPeriod == 8 || cycleInPeriod == 9)
                        {
                            // Cycle 8 & 9: Excursion (+3.2°C above target) -> 2nd consecutive abnormal triggers ReeferAlertCreated
                            returnTemp = targetTemp + tolerance + 2.4m + (decimal)(random.NextDouble() * 0.4);
                        }
                        else
                        {
                            // Cycle 10: Cooling recovery starts
                            returnTemp = targetTemp + tolerance + 0.5m;
                        }
                    }
                    else
                    {
                        // Normal realistic oscillation: target +/- 0.35°C
                        decimal drift = (decimal)((random.NextDouble() * 0.7) - 0.35);
                        returnTemp = targetTemp + drift;
                    }
                }
                else
                {
                    switch (scenario)
                    {
                        case "EXCURSION":
                            // Overheat simulation (+4°C above target)
                            returnTemp = targetTemp + tolerance + 1.5m + (decimal)(random.NextDouble() * 0.8);
                            break;

                        case "POWER_LOSS":
                            // Power cut simulation
                            powerStatus = "POWER_LOST";
                            returnTemp = targetTemp + 3.0m;
                            break;

                        case "RECOVERY":
                        default:
                            decimal drift = (decimal)((random.NextDouble() * 0.8) - 0.4);
                            returnTemp = customTemp ?? (targetTemp + drift);
                            break;
                    }
                }

                decimal supplyTemp = returnTemp - 0.3m;
                decimal ambientTemp = 32.0m + (decimal)(random.NextDouble() * 2.0);

                var telemetryDto = new RecordSimulatedTelemetryDto
                {
                    ReturnTemperature = Math.Round(returnTemp, 2),
                    SupplyTemperature = Math.Round(supplyTemp, 2),
                    AmbientTemperature = Math.Round(ambientTemp, 2),
                    DataSource = "IOT_TELEMATICS",
                    DeviceId = "IOT-REEFER-SIM-01",
                    Notes = $"Mô phỏng chu kỳ telemetry tự động (Chu kỳ: {_simulationCycleCounter})",
                    SimulatedPowerStatus = powerStatus
                };

                await RecordSimulatedTelemetryAsync(session.Id, telemetryDto, null, cancellationToken);
            }
        }
        finally
        {
            _simulationLock.Release();
        }
    }

    public Task SetDemoScenarioAsync(SimulateScenarioRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SessionId.HasValue)
        {
            _sessionScenarios[request.SessionId.Value] = (request.Scenario.ToUpperInvariant(), request.CustomTemperature);
        }
        else
        {
            _globalScenario = request.Scenario.ToUpperInvariant();
        }

        _logger.LogInformation("Demo simulation scenario updated to {Scenario} (Target: {Target})",
            request.Scenario, request.SessionId?.ToString() ?? "ALL");

        return Task.CompletedTask;
    }
}

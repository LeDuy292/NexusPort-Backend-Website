using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.Notifications.Interfaces;
using NexusPort.Modules.Yard.Preparation.Domain;

namespace NexusPort.Modules.Yard.Preparation.Infrastructure.BackgroundServices;

public class PreparationMonitoringWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PreparationMonitoringWorker> _logger;

    public PreparationMonitoringWorker(
        IServiceProvider serviceProvider,
        ILogger<PreparationMonitoringWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PreparationMonitoringWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalSeconds = 60;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Lấy cấu hình kiểm tra
                var config = await db.Set<PreparationConfig>().FirstOrDefaultAsync(stoppingToken);
                if (config != null)
                {
                    intervalSeconds = Math.Max(5, config.DelayCheckIntervalSeconds);
                }

                var now = DateTimeOffset.UtcNow;

                // 1. Kiểm tra lệnh có nguy cơ trễ (PreparationAtRisk)
                await CheckAtRiskTasksAsync(db, now, stoppingToken);

                // 2. Kiểm tra lệnh quá hạn TargetReadyAt (PreparationDelayed & KPI Exception)
                var graceMinutes = config?.RescheduleGraceMinutes ?? 15;
                await CheckDelayedTasksAsync(db, now, graceMinutes, stoppingToken);

                // 3. Quét dọn các ô giữ chỗ quá hạn mà lệnh không chạy
                await CleanExpiredSlotReservationsAsync(db, now, stoppingToken);

                // 4. Phát tán các sự kiện Outbox chưa gửi
                await DispatchOutboxMessagesAsync(scope.ServiceProvider, db, stoppingToken);

                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Lỗi khi chạy vòng lặp giám sát lệnh chuẩn bị container.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("PreparationMonitoringWorker stopped.");
    }

    private async Task CheckAtRiskTasksAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Lệnh chưa xong, đang trong khoảng 15 phút trước StartBy hoặc đã qua StartBy nhưng chưa quá TargetReadyAt
        var atRiskTasks = await db.Set<PreparationTask>()
            .Where(t => t.Status == PreparationTaskStatus.Pending ||
                        t.Status == PreparationTaskStatus.Planned ||
                        t.Status == PreparationTaskStatus.InProgress)
            .Where(t => t.StartBy <= now.AddMinutes(15) && t.TargetReadyAt > now)
            .Where(t => !t.Note!.Contains("[AtRiskNotified]"))
            .ToListAsync(ct);

        foreach (var task in atRiskTasks)
        {
            task.Note = (task.Note ?? "") + " [AtRiskNotified]";
            task.Priority += 10;

            var outboxMsg = new PreparationOutboxMessage(
                "PreparationAtRisk",
                JsonSerializer.Serialize(new
                {
                    TaskId = task.Id,
                    task.ContainerNo,
                    task.TargetReadyAt,
                    task.StartBy,
                    Alert = $"Container {task.ContainerNo} có nguy cơ trễ giờ hẹn ({task.TargetReadyAt:HH:mm dd/MM/yyyy})."
                }));

            db.Set<PreparationOutboxMessage>().Add(outboxMsg);
            _logger.LogWarning("Phát hiện lệnh chuẩn bị có nguy cơ trễ: TaskId={TaskId}, ContainerNo={ContainerNo}", task.Id, task.ContainerNo);
        }
    }

    private async Task CheckDelayedTasksAsync(AppDbContext db, DateTimeOffset now, int graceMinutes, CancellationToken ct)
    {
        // Quá TargetReadyAt mà chưa Ready
        var overdueTasks = await db.Set<PreparationTask>()
            .Where(t => t.Status != PreparationTaskStatus.Ready &&
                        t.Status != PreparationTaskStatus.Cancelled &&
                        t.Status != PreparationTaskStatus.Failed)
            .Where(t => t.TargetReadyAt < now)
            .ToListAsync(ct);

        foreach (var task in overdueTasks)
        {
            if (task.Status != PreparationTaskStatus.Delayed)
            {
                task.Status = PreparationTaskStatus.Delayed;
                task.Priority += 30;

                var outboxMsg = new PreparationOutboxMessage(
                    "PreparationDelayed",
                    JsonSerializer.Serialize(new
                    {
                        TaskId = task.Id,
                        task.ContainerNo,
                        task.TargetReadyAt,
                        Alert = $"Container {task.ContainerNo} đã quá giờ hẹn {task.TargetReadyAt:HH:mm dd/MM/yyyy} mà chưa hoàn tất chuẩn bị."
                    }));

                db.Set<PreparationOutboxMessage>().Add(outboxMsg);
            }

            // Quá hạn cộng thêm thời gian gia hạn graceMinutes: tạo ngoại lệ ghi nhận KPI
            if (now > task.TargetReadyAt.AddMinutes(graceMinutes))
            {
                bool exceptionExists = await db.Set<ExceptionItem>()
                    .AnyAsync(e => e.EntityId == task.Id && e.Category == "PreparationOverdue", ct);

                if (!exceptionExists)
                {
                    var exception = new ExceptionItem(
                        category: "PreparationOverdue",
                        reason: $"Container {task.ContainerNo} đã trễ quá {graceMinutes} phút so với giờ hẹn {task.TargetReadyAt:HH:mm dd/MM/yyyy}.",
                        severity: ExceptionSeverity.High,
                        entityId: task.Id,
                        containerNo: task.ContainerNo,
                        notes: "Báo cáo KPI trễ giờ giao hàng bãi.");

                    db.Set<ExceptionItem>().Add(exception);
                    _logger.LogError("Lập ngoại lệ KPI trễ chuẩn bị container: TaskId={TaskId}, ContainerNo={ContainerNo}", task.Id, task.ContainerNo);
                }
            }
        }
    }

    private async Task CleanExpiredSlotReservationsAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var expiredReservations = await db.Set<SlotReservation>()
            .Where(r => r.ReservedUntil < now)
            .ToListAsync(ct);

        if (expiredReservations.Count > 0)
        {
            _logger.LogInformation("Giải phóng {Count} ô bãi giữ chỗ đã quá hạn.", expiredReservations.Count);
            db.Set<SlotReservation>().RemoveRange(expiredReservations);
        }
    }

    private async Task DispatchOutboxMessagesAsync(IServiceProvider sp, AppDbContext db, CancellationToken ct)
    {
        var pendingMessages = await db.Set<PreparationOutboxMessage>()
            .Where(m => m.PublishedAt == null && m.RetryCount < 5)
            .OrderBy(m => m.OccurredAt)
            .Take(20)
            .ToListAsync(ct);

        if (pendingMessages.Count == 0) return;

        INotificationService? notificationService = null;
        try
        {
            notificationService = sp.GetService<INotificationService>();
        }
        catch { }

        foreach (var msg in pendingMessages)
        {
            try
            {
                // Gửi thông báo hệ thống nếu có
                if (notificationService != null)
                {
                    await notificationService.SendAsync(new NexusPort.Infrastructure.Notifications.DTOs.SendNotificationDto
                    {
                        RecipientId = Guid.Empty, // Broadcast to dispatchers/operators
                        Title = $"Sự kiện bãi: {msg.EventType}",
                        Message = msg.Payload,
                        Type = NexusPort.Infrastructure.Notifications.Enums.NotificationType.SystemAlert,
                        Severity = NexusPort.Infrastructure.Notifications.Enums.NotificationSeverity.Info,
                        ReferenceId = msg.EventId
                    }, ct);
                }

                msg.PublishedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("Dispatched Outbox Event {EventType} ({EventId}).", msg.EventType, msg.EventId);
            }
            catch (Exception ex)
            {
                msg.RetryCount++;
                msg.Error = ex.Message;
                _logger.LogWarning(ex, "Thất bại khi gửi Outbox Event {EventType} ({EventId}), lần thử {RetryCount}.", msg.EventType, msg.EventId, msg.RetryCount);
            }
        }
    }
}

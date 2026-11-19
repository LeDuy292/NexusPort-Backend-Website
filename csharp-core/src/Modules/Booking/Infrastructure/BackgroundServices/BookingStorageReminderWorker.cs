using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Database;

namespace NexusPort.Modules.Booking.Infrastructure.BackgroundServices;

/// <summary>
/// Sends booking-based yard-storage reminders at 09:00 Vietnam time.
/// A Dropoff booking date is day one. Only visits physically in the yard are chargeable storage candidates;
/// a pickup appointment does not stop storage until the container physically leaves the yard.
/// </summary>
public sealed class BookingStorageReminderWorker(
    IServiceScopeFactory scopes,
    ILogger<BookingStorageReminderWorker> logger) : BackgroundService
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly TimeSpan NoticeTime = new(9, 0, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BookingStorageReminderWorker started; daily notices are scheduled for 09:00 Vietnam time.");
        DateOnly? lastProcessedDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var localNow = DateTimeOffset.UtcNow.ToOffset(VietnamOffset);
            var localDate = DateOnly.FromDateTime(localNow.DateTime);
            if (localNow.TimeOfDay >= NoticeTime && lastProcessedDate != localDate)
            {
                try
                {
                    await ProcessTodayAsync(localDate, stoppingToken);
                    lastProcessedDate = localDate;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Failed to process booking-based yard-storage reminders for {LocalDate}.", localNow.Date);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("BookingStorageReminderWorker stopped.");
    }

    private async Task ProcessTodayAsync(DateOnly today, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidates = await db.Database.SqlQueryRaw<StorageBookingCandidate>("""
            SELECT DISTINCT ON (v.id)
              v.id AS "VisitId",
              b.id AS "BookingId",
              b.booking_code AS "BookingCode",
              b.carrier_id AS "CarrierId",
              c.container_no AS "ContainerNumber",
              (b.appointment_start AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS "BookingDate"
            FROM container_visits v
            JOIN booking_containers bc ON bc.container_visit_id = v.id AND bc.container_id = v.container_id
            JOIN bookings b ON b.id = bc.booking_id
            JOIN containers c ON c.id = v.container_id
            WHERE v.visit_status::text = 'active'
              AND yard_booking_direction(b.id, bc.container_id) = 'dropoff'
              AND b.status::text IN ('approved', 'ready', 'checked_in', 'completed')
              AND NOT EXISTS (
                SELECT 1
                FROM booking_containers gate_link
                JOIN export_gate_links l ON l.gate_booking_id = gate_link.booking_id
                JOIN export_booking_containers vessel_container ON vessel_container.id = l.export_container_id
                WHERE gate_link.container_visit_id = v.id
                  AND gate_link.container_id = v.container_id
                  AND vessel_container.container_id = v.container_id
                  AND vessel_container.status = 'Loaded'
              )
            ORDER BY v.id, b.appointment_start
            """).ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            var bookingDate = DateOnly.FromDateTime(candidate.BookingDate);
            var bookingDayNumber = today.DayNumber - bookingDate.DayNumber;
            var notice = GetNotice(bookingDayNumber, bookingDate, candidate);
            if (notice is null) continue;

            var companyUsers = await db.Database.SqlQueryRaw<StorageNoticeRecipient>("""
                SELECT DISTINCT u.id AS "UserId"
                FROM carrier_users cu
                JOIN users u ON u.id = cu.user_id
                WHERE cu.carrier_id = {0} AND u.is_active = true
                """, candidate.CarrierId).ToListAsync(ct);

            var portUsers = await db.Database.SqlQueryRaw<StorageNoticeRecipient>("""
                SELECT u.id AS "UserId"
                FROM users u
                WHERE u.is_active = true
                  AND u.role IN ('Administrator', 'Admin', 'Dispatcher', 'Yard Operator', 'Yard Staff', 'Gate Officer', 'Gate')
                """).ToListAsync(ct);

            foreach (var recipient in companyUsers.Select(x => x.UserId).Concat(portUsers.Select(x => x.UserId)).Distinct())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    WITH delivery AS (
                      INSERT INTO booking_storage_notice_deliveries
                        (booking_id, container_visit_id, user_id, notice_key, booking_date)
                      VALUES ({candidate.BookingId}, {candidate.VisitId}, {recipient}, {notice.Key}, {bookingDate})
                      ON CONFLICT DO NOTHING
                      RETURNING user_id
                    )
                    INSERT INTO notifications (id, receiver_user_id, title, message, is_read, created_at)
                    SELECT {Guid.NewGuid()}, delivery.user_id, {notice.Title}, {notice.Message}, false, now()
                    FROM delivery
                    """, ct);
            }
        }
    }

    private static StorageNotice? GetNotice(int bookingDayNumber, DateOnly bookingDate, StorageBookingCandidate candidate)
    {
        var lastIncludedDay = bookingDate.AddDays(4);
        var deadline = lastIncludedDay.ToDateTime(TimeOnly.MaxValue).ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("vi-VN"));
        var subject = $"Booking {candidate.BookingCode} · cont {candidate.ContainerNumber}";

        return bookingDayNumber switch
        {
            4 => new StorageNotice(
                "EXPIRING",
                "Booking bãi đến ngày cuối",
                $"{subject}: hôm nay là ngày cuối trong 5 ngày giữ vị trí theo booking. Hạn booking đến {deadline}. Vui lòng sắp xếp nhận container hoặc cập nhật kế hoạch."),
            5 => new StorageNotice(
                "OVERDUE",
                "Booking giữ vị trí đã quá hạn",
                $"{subject}: thời hạn giữ vị trí theo booking đã kết thúc. Vui lòng xác nhận kế hoạch nhận container hoặc liên hệ điều độ cảng. Phí phát sinh chưa được tính trên hệ thống."),
            6 => new StorageNotice(
                "NO_RESPONSE_1D",
                "Container còn trong bãi · cần phản hồi",
                $"{subject}: đã qua 1 ngày kể từ khi hết thời gian miễn phí nhưng hệ thống chưa ghi nhận container rời bãi. Lịch Pickup đã đặt (nếu có) chưa thay thế xác nhận Gate Out hoặc xếp tàu. Vui lòng cập nhật kế hoạch xử lý."),
            8 => new StorageNotice(
                "ESCALATED_3D",
                "Container tồn quá hạn · cần điều độ xử lý",
                $"{subject}: container vẫn chưa được ghi nhận rời bãi sau 3 ngày quá hạn. Cảng cần xác minh vị trí thực tế và chuyển hồ sơ cho trưởng ca/quản lý xử lý. Phí lưu bãi phát sinh chưa được hệ thống tính."),
            _ => null
        };
    }

    private sealed record StorageNotice(string Key, string Title, string Message);
}

public sealed class StorageBookingCandidate
{
    public Guid VisitId { get; set; }
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid CarrierId { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public DateTime BookingDate { get; set; }
}

public sealed class StorageNoticeRecipient
{
    public Guid UserId { get; set; }
}

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Database;

namespace NexusPort.Modules.Booking.Infrastructure.BackgroundServices;

/// <summary>
/// Quá hạn xác nhận B/L draft mà shipper không phản hồi: coi như đã chấp nhận theo điều khoản đã thông báo,
/// và ghi rõ trong lịch sử hồ sơ (kênh SYSTEM) để có căn cứ khi tranh chấp.
/// </summary>
public sealed class ExportDraftDeadlineWorker(IServiceScopeFactory scopes, ILogger<ExportDraftDeadlineWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await AcceptOverdueDrafts(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Không xử lý được các draft B/L quá hạn xác nhận."); }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task AcceptOverdueDrafts(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var overdue = await db.Database.SqlQueryRaw<string>("""
            SELECT json_build_object('id',b.id,'bookingNo',b.booking_no,'carrierId',b.carrier_id,'draftVersion',b.draft_version)::text AS "Value"
            FROM export_bookings b
            WHERE b.si_status='Draft' AND b.draft IS NOT NULL AND b.draft_deadline IS NOT NULL AND b.draft_deadline < now()
              AND b.status NOT IN ('Pending','Rejected','Cancelled','BlIssued') LIMIT 50
            """).ToListAsync(ct);
        foreach (var json in overdue)
        {
            var row = JsonNode.Parse(json)!.AsObject();
            var id = Guid.Parse(row["id"]!.GetValue<string>());
            var bookingNo = row["bookingNo"]!.GetValue<string>();
            var version = row["draftVersion"]!.GetValue<int>();
            // Điều kiện si_status='Draft' trong WHERE chống đua với lúc shipper vừa xác nhận hoặc gửi yêu cầu sửa
            var changed = await db.Database.ExecuteSqlRawAsync(
                "UPDATE export_bookings SET si_status='Confirmed',version=version+1,updated_at=now() WHERE id={0} AND si_status='Draft' AND draft_deadline < now()", [id], ct);
            if (changed != 1) continue;
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO shipment_document_events(direction,file_ref,document_type,action,from_status,to_status,channel,document_version,note)
                VALUES('EXPORT',{0},'DRAFT_BL','AUTO_ACCEPTED','Draft','Confirmed','SYSTEM',{1},'Quá hạn xác nhận mà shipper không phản hồi: coi như đã chấp nhận draft theo điều khoản đã thông báo.')
                """, [bookingNo, version], ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO cargo_notifications(recipient_company_id,company_tax_code,recipient_role,event_type,direction,priority,title,message,data,action_type,action_label,action_url,document_type,document_id,dedup_key)
                SELECT c.id,c.tax_code,'Transport Company','DraftBLAutoAccepted','export','high',
                       'B/L draft [' || {0} || '] được coi là đã chấp nhận',
                       'Đã quá hạn xác nhận B/L draft và hệ thống chưa nhận phản hồi, nên draft được coi là đã chấp nhận theo điều khoản đã thông báo.',
                       jsonb_build_object('bookingNo',{0}::text),'view_details','Xem chi tiết','/booking?bookingNo=' || {0},'BOOKING',{0}::text,'draft-auto-' || {0} || '-v' || {1}::text
                FROM carriers c WHERE c.id={2}::uuid ON CONFLICT (dedup_key) DO NOTHING
                """, [bookingNo, version, Guid.Parse(row["carrierId"]!.GetValue<string>())], ct);
            logger.LogInformation("Draft B/L {BookingNo} quá hạn xác nhận, đã tự chấp nhận.", bookingNo);
        }
    }
}

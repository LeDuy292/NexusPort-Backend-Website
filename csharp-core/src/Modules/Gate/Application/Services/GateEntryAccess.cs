using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Modules.Gate.Domain.Entities;
using NexusPort.Modules.Gate.Domain.Rules;
using System.Text.RegularExpressions;

namespace NexusPort.Modules.Gate.Application.Services;

public class GateEntryAccess(AppDbContext context)
{
    private const string SelectOrder = @"SELECT e.id AS ""Id"",e.booking_id AS ""BookingId"",e.driver_id AS ""DriverId"",
      e.truck_id AS ""TruckId"",e.vehicle_plate AS ""VehiclePlate"",e.status AS ""Status"",
      e.valid_from AS ""ValidFrom"",e.valid_until AS ""ValidUntil"",e.issued_at AS ""IssuedAt"",
      b.booking_code AS ""BookingCode"",d.full_name AS ""DriverName""
      FROM gate_entry_orders e JOIN bookings b ON b.id=e.booking_id JOIN drivers d ON d.id=e.driver_id ";

    public Task<GateEntryOrder?> LatestAsync(Guid bookingId, CancellationToken token) => context.Database
        .SqlQueryRaw<GateEntryOrder>(SelectOrder + "WHERE e.booking_id={0} ORDER BY e.issued_at DESC,e.dispatch_version DESC LIMIT 1",bookingId)
        .FirstOrDefaultAsync(token);

    public Task<List<GateEntryOrder>> QueueAsync(CancellationToken token) => context.Database
        .SqlQueryRaw<GateEntryOrder>(SelectOrder + "WHERE e.issued_at>now()-interval '24 hours' ORDER BY e.issued_at DESC LIMIT 200")
        .ToListAsync(token);

    public async Task<Guid?> ResolveBookingAsync(string plate, CancellationToken token, bool gateOut=false)
    {
        if(gateOut)
        {
            var inside=await context.Database.SqlQueryRaw<Guid>(@"SELECT b.id AS ""Value"" FROM bookings b
              LEFT JOIN trucks t ON t.id=b.truck_id WHERE upper(regexp_replace(COALESCE(t.plate_number,b.vehicle_plate,''),'[^a-zA-Z0-9]','','g'))={0}
              AND EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_in' AND g.status='completed')
              AND NOT EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_out' AND g.status='completed')
              ORDER BY b.created_at DESC LIMIT 2",Normalize(plate)).ToListAsync(token);
            return inside.Count==1 ? inside[0] : null;
        }
        var rows = await context.Database.SqlQueryRaw<Guid>(@"SELECT e.booking_id AS ""Value"" FROM gate_entry_orders e
          JOIN bookings b ON b.id=e.booking_id WHERE e.vehicle_plate={0} AND e.status IN ('pending_ack','active')
          AND e.valid_until>now() AND b.status::text IN ('approved','ready') ORDER BY e.issued_at DESC LIMIT 2",Normalize(plate)).ToListAsync(token);
        // Ambiguous plates must be resolved by an explicit booking, never by first row.
        return rows.Count==1 ? rows[0] : null;
    }

    public static string Normalize(string? plate) => Regex.Replace(plate ?? "", "[^a-zA-Z0-9]", "").ToUpperInvariant();

    // The API enables Npgsql legacy timestamp behavior: timestamptz is read as
    // Local DateTime. DateTime operators compare clock ticks, not UTC instants.
    public static DateTime Utc(DateTime value) => value.ToUniversalTime();
    public static bool IsFreshVerification(DateTime verifiedAt, DateTime now)
    {
        var instant=Utc(verifiedAt);
        var clock=Utc(now);
        return instant>=clock.AddMinutes(-2) && instant<=clock.AddSeconds(30);
    }

    public static GateRuleResult Check(GateEntryOrder? order, Booking.Domain.Entities.Booking booking, string plate, DateTime now)
    {
        const string rule = "GateEntryOrderRule";
        if (order==null) return GateRuleResult.Fail(rule,"ENTRY_ORDER_MISSING","Chưa có lệnh vào cổng từ điều độ. Vui lòng chờ điều độ gọi lại.");
        if (booking.Status!=BookingStatus.Approved && booking.Status!=BookingStatus.Ready)
            return GateRuleResult.Fail(rule,"ENTRY_BOOKING_NOT_READY","Booking không còn chờ vào cổng.");
        var clock=Utc(now);
        if (order.Status=="pending_ack" && clock<Utc(order.ValidUntil)) return GateRuleResult.Fail(rule,"ENTRY_ACK_REQUIRED","Tài xế cần nhận lệnh gọi trên app trước khi vào cổng.");
        if (order.Status!="active" || clock<Utc(order.ValidFrom) || clock>=Utc(order.ValidUntil))
            return GateRuleResult.Fail(rule,"ENTRY_ORDER_UNAVAILABLE","Lệnh vào cổng đã hết hạn, bị thu hồi hoặc đã sử dụng. Vui lòng chờ điều độ gọi lại.");
        if (order.BookingId!=booking.Id || order.DriverId!=booking.DriverId || order.TruckId!=booking.TruckId || Normalize(plate)!=order.VehiclePlate)
            return GateRuleResult.Fail(rule,"ENTRY_ASSIGNMENT_MISMATCH","Xe hoặc tài xế không khớp lệnh vào cổng.");
        return GateRuleResult.Pass(rule,"Lệnh vào cổng đã được tài xế nhận và còn hiệu lực.");
    }
}

public class GateEntryOrderRule(AppDbContext database) : IGateRule
{
    public string RuleName => "GateEntryOrderRule";
    public int Priority => 15;
    public async Task<GateRuleResult> EvaluateAsync(GateValidationContext context, CancellationToken cancellationToken = default)
    {
        var isExit=string.Equals(context.GateType,"GateOut",StringComparison.OrdinalIgnoreCase);
        var expected=isExit
            ? await database.Database.SqlQueryRaw<string>("SELECT code AS \"Value\" FROM gates WHERE is_active AND code IN ('GATE-02','GATE-03') ORDER BY (code='GATE-02') DESC LIMIT 1").FirstOrDefaultAsync(cancellationToken) ?? "GATE-02"
            : "GATE-01";
        if(!string.Equals(context.GateCode,expected,StringComparison.OrdinalIgnoreCase))
            return GateRuleResult.Fail(RuleName,"GATE_DIRECTION_MISMATCH",$"Luồng này chỉ sử dụng cổng {expected}.");
        if (!string.Equals(context.GateType,"GateIn",StringComparison.OrdinalIgnoreCase) || context.Booking==null)
            return GateRuleResult.Pass(RuleName);
        var order = await new GateEntryAccess(database).LatestAsync(context.Booking.Id,cancellationToken);
        return GateEntryAccess.Check(order,context.Booking,context.VehiclePlate,DateTime.UtcNow);
    }
}

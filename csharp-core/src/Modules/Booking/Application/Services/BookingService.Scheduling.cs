using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;

namespace NexusPort.Modules.Booking.Application.Services;

public partial class BookingService
{
    private async Task<List<BookingContainerReservation>> ContainerReservationsAsync(Guid carrierId, BookingPlanningRequest request, CancellationToken token, BookingCargoSource source)
    {
        var rows=await _context.Database.SqlQueryRaw<BookingContainerReservation>("""
            SELECT b.appointment_start AS "StartsAt",b.appointment_end AS "EndsAt",false AS "IsStay",
              CASE WHEN b.carrier_id={0} THEN b.id END AS "BookingId",
              CASE WHEN b.carrier_id={0} THEN b.booking_code END AS "BookingCode"
            FROM bookings b WHERE b.id<>{2} AND b.status::text NOT IN ('canceled','rejected','expired','completed')
            AND (EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.booking_id=b.id AND bc.container_id={1})
              OR EXISTS(SELECT 1 FROM booking_operations o WHERE o.booking_id=b.id
                AND (o.container_id={1} OR upper(trim(o.container_number))=(SELECT upper(container_no) FROM containers WHERE id={1}))))
            """,carrierId,request.ContainerId,request.BookingId??Guid.Empty).ToListAsync(token);
        foreach(var row in rows)NormalizeReservation(row);
        if(request.BookingType=="Pickup")
        {
            var inboundEnd=await _context.Database.SqlQueryRaw<DateTime?>("""
                SELECT max(b.appointment_end) AS "Value" FROM bookings b JOIN booking_containers bc ON bc.booking_id=b.id
                WHERE bc.container_visit_id={0} AND yard_booking_direction(b.id,bc.container_id)='dropoff' AND b.status::text NOT IN ('canceled','rejected','expired')
                """,source.ContainerVisitId??Guid.Empty).SingleAsync(token);
            if(inboundEnd.HasValue)rows.Add(new BookingContainerReservation {StartsAt=DateTime.MinValue,EndsAt=ScheduleUtc(inboundEnd.Value)});
            return rows;
        }
        var stays=await _context.Database.SqlQueryRaw<BookingContainerReservation>("""
            SELECT COALESCE(min(b.appointment_start) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='dropoff'),v.started_at) AS "StartsAt",
              max(b.appointment_end) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='pickup') AS "EndsAt",true AS "IsStay",
              (array_agg(b.id ORDER BY b.appointment_start) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='dropoff' AND b.carrier_id={0}))[1] AS "BookingId",
              (array_agg(b.booking_code ORDER BY b.appointment_start) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='dropoff' AND b.carrier_id={0}))[1] AS "BookingCode"
            FROM container_visits v LEFT JOIN booking_containers bc ON bc.container_visit_id=v.id
            LEFT JOIN bookings b ON b.id=bc.booking_id AND b.status::text NOT IN ('canceled','rejected','expired')
            WHERE v.container_id={1} AND v.visit_status IN ('planned','active')
              AND NOT EXISTS(SELECT 1 FROM booking_containers mine WHERE mine.container_visit_id=v.id AND mine.booking_id={2})
            GROUP BY v.id,v.started_at
            HAVING min(b.appointment_start) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='dropoff') IS NOT NULL OR v.started_at IS NOT NULL
            """,carrierId,request.ContainerId,request.BookingId??Guid.Empty).ToListAsync(token);
        foreach(var row in stays)NormalizeReservation(row);
        rows.AddRange(stays);
        return rows;
    }

    // The API enables Npgsql legacy timestamp behavior. timestamptz values may
    // arrive as local DateTimes; compare actual instants with the UTC API window.
    private static DateTime ScheduleUtc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value,DateTimeKind.Utc);

    private static void NormalizeReservation(BookingContainerReservation row)
    {
        row.StartsAt=ScheduleUtc(row.StartsAt);
        if(row.EndsAt.HasValue)row.EndsAt=ScheduleUtc(row.EndsAt.Value);
    }

    private static BookingContainerReservation? BlockingContainer(IEnumerable<BookingContainerReservation> rows,DateTime start,DateTime end) =>
        rows.Where(row=>row.StartsAt<end&&(!row.EndsAt.HasValue||row.EndsAt>start))
            .OrderBy(row=>row.EndsAt.HasValue).ThenBy(row=>row.StartsAt).FirstOrDefault();

    private static string ContainerConflictMessage(BookingCargoSource source,BookingContainerReservation conflict) =>
        conflict.StartsAt==DateTime.MinValue
            ? $"Container Nhận {source.ContainerNumber} chỉ có thể đặt từ {conflict.EndsAt!.Value.AddHours(7):HH:mm dd/MM/yyyy} (giờ Việt Nam), sau khung giờ Giao đã đăng ký."
            : conflict.IsStay&&!conflict.EndsAt.HasValue
            ? $"Container {source.ContainerNumber} đã có lượt bãi chưa có kế hoạch lấy ra. Hãy xem/sửa Booking hiện có hoặc hoàn tất kế hoạch Pickup trước khi đặt lượt Giao mới."
            : $"Container {source.ContainerNumber} đã có lịch giao/nhận trùng giờ. Chọn khung giờ gợi ý hoặc sửa Booking hiện có. Các xe và container khác vẫn có thể đặt cùng khung giờ.";

    private async Task<BookingScheduleAssessment> AssessSchedulesAsync(Guid carrierId,List<(BookingPlanningRequest Request,BookingCargoSource Source)> legs,BookingFleetOptions fleet,CancellationToken token)
    {
        var request=legs[0].Request;
        var reservations=new List<BookingContainerReservation>();
        var conflicts=new List<(BookingCargoSource Source,BookingContainerReservation Conflict)>();
        foreach(var leg in legs)
        {
            var rows=await ContainerReservationsAsync(carrierId,leg.Request,token,leg.Source);
            reservations.AddRange(rows);
            var conflict=BlockingContainer(rows,request.AppointmentStart,request.AppointmentEnd);
            if(conflict!=null)conflicts.Add((leg.Source,conflict));
        }
        // Report an indefinite occupied stay first: moving the appointment alone
        // cannot resolve it. Keep the other leg's timing constraint visible too.
        var ordered=conflicts.OrderByDescending(leg=>leg.Conflict.IsStay&&!leg.Conflict.EndsAt.HasValue).ToList();
        var blocked=ordered.FirstOrDefault().Conflict;
        var result=new BookingScheduleAssessment {
            IsAvailable=blocked==null&&fleet.RecommendedTruckId.HasValue&&fleet.RecommendedDriverId.HasValue&&request.AppointmentStart>=DateTime.UtcNow.AddMinutes(-10),
            RequiresExistingBookingResolution=reservations.Any(row=>row.IsStay&&!row.EndsAt.HasValue&&row.StartsAt<request.AppointmentEnd),
            Message=blocked==null?fleet.Message:string.Join("\n",ordered.Select(leg=>ContainerConflictMessage(leg.Source,leg.Conflict))),
            ExistingBookingId=blocked?.BookingId,ExistingBookingCode=blocked?.BookingCode
        };
        if(result.IsAvailable)return result;
        // An unfinished stay has no known release time. Do not invent a free slot later today.
        if(result.RequiresExistingBookingResolution)return result;
        var duration=request.AppointmentEnd-request.AppointmentStart;
        var horizon=request.AppointmentStart.AddDays(7);
        var events=await _context.Database.SqlQueryRaw<DateTime>("""
            SELECT b.appointment_end AS "Value" FROM bookings b
            WHERE b.carrier_id={0} AND b.id<>{3} AND b.status::text NOT IN ('canceled','rejected','expired','completed')
              AND b.appointment_end>{1} AND b.appointment_end<={2}
            UNION SELECT t.appointment_end AS "Value" FROM transport_trips t
            WHERE t.status IN ('Assigned','Acknowledged') AND t.appointment_end>{1} AND t.appointment_end<={2}
              AND (EXISTS(SELECT 1 FROM trucks truck WHERE truck.id=t.truck_id AND truck.carrier_id={0})
                OR EXISTS(SELECT 1 FROM drivers d WHERE d.user_id=t.driver_id AND d.carrier_id={0}))
            """,carrierId,request.AppointmentStart,horizon,request.BookingId??Guid.Empty).ToListAsync(token);
        var candidates=events.Select(ScheduleUtc).Concat(reservations.Where(row=>row.EndsAt.HasValue).Select(row=>row.EndsAt!.Value))
            .Concat(Enumerable.Range(1,7).Select(day=>request.AppointmentStart.AddDays(day)))
            .Where(start=>start>request.AppointmentStart&&start>=DateTime.UtcNow&&start<=horizon)
            // The gate form uses a single date, in Vietnam time (UTC+7).
            .Where(start=>start.AddHours(7).Date==(start+duration).AddHours(7).Date)
            .Distinct().OrderBy(start=>start).Take(24);
        foreach(var start in candidates)
        {
            var end=start+duration;
            if(BlockingContainer(reservations,start,end)!=null)continue;
            var available=await LoadBookingFleetAsync(carrierId,start,end,request.BookingId,token);
            if(!available.RecommendedTruckId.HasValue||!available.RecommendedDriverId.HasValue)continue;
            result.Alternatives.Add(new BookingTimeOption {AppointmentStart=start,AppointmentEnd=end,
                TruckPlate=available.Trucks.Single(truck=>truck.Id==available.RecommendedTruckId).PlateNumber,
                DriverName=available.Drivers.Single(driver=>driver.Id==available.RecommendedDriverId).FullName});
            if(result.Alternatives.Count==3)break;
        }
        return result;
    }
}

public sealed class BookingContainerReservation
{
    public DateTime StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public bool IsStay { get; set; }
    public Guid? BookingId { get; set; }
    public string? BookingCode { get; set; }
}

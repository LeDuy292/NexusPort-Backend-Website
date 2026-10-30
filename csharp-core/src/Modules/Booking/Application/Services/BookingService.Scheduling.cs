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
        if(request.BookingType=="Pickup")
        {
            var inboundEnd=await _context.Database.SqlQueryRaw<DateTime?>("""
                SELECT max(b.appointment_end) AS "Value" FROM bookings b JOIN booking_containers bc ON bc.booking_id=b.id
                WHERE bc.container_visit_id={0} AND b.booking_type::text='dropoff' AND b.status::text NOT IN ('canceled','rejected','expired')
                """,source.ContainerVisitId??Guid.Empty).SingleAsync(token);
            if(inboundEnd.HasValue)rows.Add(new BookingContainerReservation {StartsAt=DateTime.MinValue,EndsAt=inboundEnd});
            return rows;
        }
        var stays=await _context.Database.SqlQueryRaw<BookingContainerReservation>("""
            SELECT COALESCE(min(b.appointment_start) FILTER(WHERE b.booking_type::text='dropoff'),v.started_at) AS "StartsAt",
              max(b.appointment_end) FILTER(WHERE b.booking_type::text='pickup') AS "EndsAt",true AS "IsStay",
              (array_agg(b.id ORDER BY b.appointment_start) FILTER(WHERE b.booking_type::text='dropoff' AND b.carrier_id={0}))[1] AS "BookingId",
              (array_agg(b.booking_code ORDER BY b.appointment_start) FILTER(WHERE b.booking_type::text='dropoff' AND b.carrier_id={0}))[1] AS "BookingCode"
            FROM container_visits v LEFT JOIN booking_containers bc ON bc.container_visit_id=v.id
            LEFT JOIN bookings b ON b.id=bc.booking_id AND b.status::text NOT IN ('canceled','rejected','expired')
            WHERE v.container_id={1} AND v.visit_status IN ('planned','active')
              AND NOT EXISTS(SELECT 1 FROM booking_containers mine WHERE mine.container_visit_id=v.id AND mine.booking_id={2})
            GROUP BY v.id,v.started_at
            HAVING min(b.appointment_start) FILTER(WHERE b.booking_type::text='dropoff') IS NOT NULL OR v.started_at IS NOT NULL
            """,carrierId,request.ContainerId,request.BookingId??Guid.Empty).ToListAsync(token);
        rows.AddRange(stays);
        return rows;
    }

    private static BookingContainerReservation? BlockingContainer(IEnumerable<BookingContainerReservation> rows,DateTime start,DateTime end) =>
        rows.Where(row=>row.StartsAt<end&&(!row.EndsAt.HasValue||row.EndsAt>start))
            .OrderBy(row=>row.EndsAt.HasValue).ThenBy(row=>row.StartsAt).FirstOrDefault();

    private static string ContainerConflictMessage(BookingCargoSource source,BookingContainerReservation conflict) =>
        conflict.StartsAt==DateTime.MinValue
            ? $"Khung giờ Nhận container {source.ContainerNumber} phải bắt đầu sau khi kết thúc khung giờ Giao. Hãy chọn khung giờ gợi ý."
            : conflict.IsStay&&!conflict.EndsAt.HasValue
            ? $"Container {source.ContainerNumber} đã có lượt bãi chưa có kế hoạch lấy ra. Hãy xem/sửa Booking hiện có hoặc hoàn tất kế hoạch Pickup trước khi đặt lượt Giao mới."
            : $"Container {source.ContainerNumber} đã có lịch giao/nhận trùng giờ. Chọn khung giờ gợi ý hoặc sửa Booking hiện có. Các xe và container khác vẫn có thể đặt cùng khung giờ.";

    private async Task<BookingScheduleAssessment> AssessScheduleAsync(Guid carrierId,BookingPlanningRequest request,BookingCargoSource source,BookingFleetOptions fleet,CancellationToken token)
    {
        var reservations=await ContainerReservationsAsync(carrierId,request,token,source);
        var blocked=BlockingContainer(reservations,request.AppointmentStart,request.AppointmentEnd);
        var result=new BookingScheduleAssessment {
            IsAvailable=blocked==null&&fleet.RecommendedTruckId.HasValue&&fleet.RecommendedDriverId.HasValue&&request.AppointmentStart>=DateTime.UtcNow.AddMinutes(-10),
            Message=blocked==null?fleet.Message:ContainerConflictMessage(source,blocked),
            ExistingBookingId=blocked?.BookingId,ExistingBookingCode=blocked?.BookingCode
        };
        if(result.IsAvailable)return result;
        // An unfinished stay has no known release time. Do not invent a free slot later today.
        if(reservations.Any(row=>row.IsStay&&!row.EndsAt.HasValue&&row.StartsAt<request.AppointmentEnd))return result;
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
        var candidates=events.Concat(reservations.Where(row=>row.EndsAt.HasValue).Select(row=>row.EndsAt!.Value))
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

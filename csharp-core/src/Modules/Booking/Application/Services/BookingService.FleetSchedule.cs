using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public partial class BookingService
{
    private async Task EnsureBookingNotDispatchedAsync(Guid id, CancellationToken ct)
    {
        var dispatched = await _context.Database.SqlQueryRaw<bool>("""
            SELECT (EXISTS(SELECT 1 FROM transport_trips t WHERE t.booking_id={0} AND t.status NOT IN ('Completed','Cancelled'))
              OR EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id={0} AND g.gate_type::text='gate_in' AND g.status::text='completed'
                AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=g.booking_id AND x.gate_type::text='gate_out'
                  AND x.status::text='completed' AND x.checked_at>=g.checked_at))) AS "Value"
            """, id).SingleAsync(ct);
        if (dispatched) throw new ValidationException("TripStatus", "Chuyến đã phát lệnh vận chuyển hoặc đã vào cảng. Chưa hỗ trợ đổi phân công khi chuyến đang thực hiện.");
    }

    public async Task<BookingFleetOptions> GetAssignmentOptionsAsync(Guid id, Guid company, CancellationToken ct)
    {
        var booking = await _context.Set<Domain.Entities.Booking>().AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == id && b.CarrierId == company, ct)
            ?? throw new NotFoundException("Booking", id);
        if (booking.Status is not (Domain.Enums.BookingStatus.Pending or Domain.Enums.BookingStatus.Ready or Domain.Enums.BookingStatus.Approved)
            || booking.TripStatus is "Arrived" or "Processing" or "PartiallyCompleted" or "Completed")
            throw new ValidationException("Status", "Chuyến đã vào cảng hoặc đã kết thúc không thể đổi phân công tại đây.");
        await EnsureBookingNotDispatchedAsync(id, ct);
        return await LoadBookingFleetAsync(company, ScheduleUtc(booking.AppointmentStart), ScheduleUtc(booking.AppointmentEnd), id, ct, signDocuments: false);
    }

    public async Task<FleetScheduleDto> GetFleetScheduleAsync(Guid company, DateTime from, DateTime to, CancellationToken ct)
    {
        from = from.ToUniversalTime(); to = to.ToUniversalTime();
        BookingFleetPolicy.ValidateWindow(from, to);
        // The read model omits documents and snapshots; it uses the same availability policy as booking validation.
        var available = await LoadBookingFleetAsync(company, from, to, null, ct, signDocuments: false);
        var drivers = await _context.Set<Driver.Domain.Entities.Driver>().AsNoTracking().Where(d => d.CarrierId == company)
            .Select(d => new FleetResourceDto { Id = d.Id, Label = d.FullName, Phone = d.Phone, Status = d.Status.ToString() }).ToListAsync(ct);
        var vehicles = await _context.Set<Vehicle.Domain.Entities.Vehicle>().AsNoTracking().Where(v => v.CarrierId == company)
            .Select(v => new FleetResourceDto { Id = v.Id, Label = v.PlateNumber, Status = v.Status.ToString() }).ToListAsync(ct);
        var freeDrivers = available.Drivers.Select(d => d.Id).ToHashSet();
        var freeVehicles = available.Trucks.Select(v => v.Id).ToHashSet();
        foreach (var driver in drivers) driver.IsAvailable = freeDrivers.Contains(driver.Id);
        foreach (var vehicle in vehicles) vehicle.IsAvailable = freeVehicles.Contains(vehicle.Id);
        var items = await _context.Database.SqlQueryRaw<FleetScheduleItem>("""
            SELECT b.id AS "Id",b.id AS "BookingId",'Booking' AS "Source",b.booking_code AS "Code",
              b.status::text AS "Status",COALESCE(b.trip_status,'Scheduled') AS "TripStatus",b.booking_type::text AS "BookingType",
              b.appointment_start AS "StartsAt",b.appointment_end AS "EndsAt",b.driver_id AS "DriverId",
              COALESCE(b.truck_id,b.vehicle_id,bt.id) AS "VehicleId",b.driver_name AS "DriverName",b.vehicle_plate AS "VehiclePlate",
              (b.status::text IN ('pending','ready','approved')
                AND NOT EXISTS(SELECT 1 FROM transport_trips t WHERE t.booking_id=b.id AND t.status NOT IN ('Completed','Cancelled'))
                AND NOT EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type::text='gate_in' AND g.status::text='completed'
                  AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=b.id AND x.gate_type::text='gate_out'
                    AND x.status::text='completed' AND x.checked_at>=g.checked_at))) AS "CanReassign"
            FROM bookings b
            LEFT JOIN trucks bt ON b.truck_id IS NULL AND b.vehicle_id IS NULL AND b.vehicle_plate IS NOT NULL
              AND bt.carrier_id={0} AND regexp_replace(upper(bt.plate_number),'[^A-Z0-9]','','g')=regexp_replace(upper(b.vehicle_plate),'[^A-Z0-9]','','g')
            WHERE b.carrier_id={0} AND (
              (b.status::text NOT IN ('canceled','rejected','expired','completed') AND b.appointment_start<{2} AND b.appointment_end>{1})
              OR b.status::text='checked_in'
              OR (b.status::text NOT IN ('canceled','rejected','expired','completed') AND b.trip_status IN ('Arrived','Processing','PartiallyCompleted'))
              OR EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type::text='gate_in' AND g.status::text='completed'
                AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=b.id AND x.gate_type::text='gate_out'
                  AND x.status::text='completed' AND x.checked_at>=g.checked_at)))
            UNION ALL
            SELECT t.id,t.booking_id,'Transport',t.trip_code,t.status,t.status,COALESCE(t.booking_type,''),
              t.appointment_start,t.appointment_end,d.id,COALESCE(t.truck_id,tt.id),d.full_name,t.truck_plate,false
            FROM transport_trips t
            LEFT JOIN drivers d ON d.user_id=t.driver_id AND d.carrier_id={0}
            LEFT JOIN trucks tt ON t.truck_id IS NULL AND t.truck_plate IS NOT NULL AND tt.carrier_id={0}
              AND regexp_replace(upper(tt.plate_number),'[^A-Z0-9]','','g')=regexp_replace(upper(t.truck_plate),'[^A-Z0-9]','','g')
            WHERE (d.id IS NOT NULL OR EXISTS(SELECT 1 FROM trucks own WHERE own.id=t.truck_id AND own.carrier_id={0}) OR tt.id IS NOT NULL)
              AND t.status NOT IN ('Completed','Cancelled')
              AND (t.status NOT IN ('Assigned','Acknowledged') OR t.appointment_start IS NULL OR t.appointment_end IS NULL
                OR (t.appointment_start<{2} AND t.appointment_end>{1}))
            UNION ALL
            SELECT sd.id,NULL::uuid,'Service',sd.number,COALESCE(os.status,'DRAFT'),'Scheduled',sd.flow,
              (trip->>'appointmentStart')::timestamptz,(trip->>'appointmentEnd')::timestamptz,
              NULLIF(trip->>'driverId','')::uuid,NULLIF(trip->>'truckId','')::uuid,NULL::text,NULL::text,false
            FROM eport_service_drafts sd LEFT JOIN eport_service_order_states os ON os.draft_id=sd.id
            CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(sd.content->'delivery'->'trips')='array'
              THEN sd.content->'delivery'->'trips' ELSE '[]'::jsonb END) AS trip
            WHERE sd.carrier_id={0} AND (os.status IS NULL OR os.status NOT IN ('CANCELLED','REJECTED'))
              AND NULLIF(trip->>'truckId','') IS NOT NULL AND NULLIF(trip->>'driverId','') IS NOT NULL
              AND trip->>'appointmentStart' LIKE '____-__-__%' AND trip->>'appointmentEnd' LIKE '____-__-__%'
              AND (trip->>'appointmentStart')::timestamptz<{2} AND (trip->>'appointmentEnd')::timestamptz>{1}
            """, company, from, to).ToListAsync(ct);
        var driverMap = drivers.ToDictionary(d => d.Id);
        var vehicleMap = vehicles.ToDictionary(v => v.Id);
        foreach (var item in items)
        {
            if (item.StartsAt.HasValue) item.StartsAt = ScheduleUtc(item.StartsAt.Value);
            if (item.EndsAt.HasValue) item.EndsAt = ScheduleUtc(item.EndsAt.Value);
            if (item.DriverId.HasValue && driverMap.TryGetValue(item.DriverId.Value, out var driver)) item.DriverName = driver.Label;
            if (item.VehicleId.HasValue && vehicleMap.TryGetValue(item.VehicleId.Value, out var vehicle)) item.VehiclePlate = vehicle.Label;
            // Physical arrival is a separate workflow; only bookings before Gate In can be reassigned here.
            if (item.TripStatus is "Arrived" or "Processing" or "PartiallyCompleted" or "Completed") item.CanReassign = false;
        }
        return new FleetScheduleDto { From = from, To = to, Items = items.OrderBy(i => i.StartsAt).ThenBy(i => i.Code).ToList(),
            Drivers = drivers.OrderBy(d => d.Label).ToList(), Vehicles = vehicles.OrderBy(v => v.Label).ToList() };
    }
}

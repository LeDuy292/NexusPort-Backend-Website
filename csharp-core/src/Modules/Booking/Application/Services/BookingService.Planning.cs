using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public partial class BookingService
{
    private async Task<decimal> PrepareSingleBookingCargoAsync(CreateBookingDto dto, CancellationToken token)
    {
        if (dto.IsCombined || dto.Operations.Count > 1)
            throw new ValidationException("Operations", "Chuyến kết hợp hai chiều chưa được hỗ trợ. Vui lòng đặt riêng lịch Giao và lịch Nhận.");
        if (dto.ContainerSelections.Count != 1)
            throw new ValidationException("ContainerSelections", "Chọn đúng một bản khai hoặc lượt bãi cho chuyến xe.");
        var selection = dto.ContainerSelections[0];
        await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM containers WHERE id={0} FOR UPDATE", selection.ContainerId).ToListAsync(token);
        if (dto.BookingType == BookingType.Dropoff)
            await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM container_declarations WHERE id={0} FOR UPDATE", selection.DeclarationId).ToListAsync(token);
        var planningRequest = new BookingPlanningRequest {
            ContainerId=selection.ContainerId, DeclarationId=selection.DeclarationId,
            YardCode=selection.YardCode, BookingType=dto.BookingType.ToString(),
            AppointmentStart=dto.AppointmentStart, AppointmentEnd=dto.AppointmentEnd };
        var source = await ResolveBookingSourceAsync(dto.CarrierId, planningRequest, token);
        var conflict=BlockingContainer(await ContainerReservationsAsync(dto.CarrierId,planningRequest,token,source),dto.AppointmentStart,dto.AppointmentEnd);
        if(conflict!=null)throw new ValidationException("Appointment",ContainerConflictMessage(source,conflict));
        if (!source.GrossWeightKg.HasValue || source.GrossWeightKg <= 0)
            throw new ValidationException("GrossWeightKg", "Bổ sung khối lượng thực tế ở bản khai trước khi đặt lịch.");
        var operationType = dto.BookingType == BookingType.Dropoff ? "Export" : "Import";
        if (dto.Operations.Count == 1 && dto.Operations[0].OperationType != operationType)
            throw new ValidationException("Operations", "Tác nghiệp không khớp loại lịch Giao/Nhận.");
        if (dto.Operations.Count == 0) dto.Operations.Add(new CreateBookingOperationDto { OperationType=operationType });
        var op = dto.Operations[0];
        op.ContainerId=source.ContainerId; op.ContainerNumber=source.ContainerNumber;
        op.ContainerIsoSize=source.ContainerTypeCode; op.CargoStatus="Loaded";
        op.CargoType=source.CargoType switch { "general"=>"General", "reefer"=>"Reefer", "dangerous" or "dangerous_goods"=>"DangerousGoods", "oversized"=>"Oversized", "perishable"=>"Perishable", "overweight"=>"Overweight", _=>throw new ValidationException("CargoType", "Bản khai chưa xác định loại hàng.") };
        op.GrossWeightKg=source.GrossWeightKg; op.SealNumber=source.SealNumber;
        op.SequenceNumber=1; op.ExpectedGateDirection=dto.BookingType == BookingType.Dropoff ? "GateIn" : "GateOut";
        selection.ContainerNumber=source.ContainerNumber; selection.ContainerTypeCode=source.ContainerTypeCode;
        selection.CargoTypeName=op.CargoType; dto.ContainerIds=new() {source.ContainerId};
        if (dto.BookingType == BookingType.Dropoff) selection.DeclarationId=source.DeclarationId;
        dto.IsReeferPowerRequired=dto.IsReeferPowerRequired && dto.BookingType == BookingType.Dropoff && source.CargoType == "reefer";
        if(dto.IsReeferPowerRequired && !(source.RequiredPowerKw > 0))
            throw new ValidationException("RequiredPowerKw", "Bổ sung công suất cấp điện hợp lệ trong khai báo container trước khi đăng ký cấp điện tại bãi.");
        dto.RequiredPowerKw=source.RequiredPowerKw;
        if (op.CargoType == "Reefer")
        {
            dto.TargetTemperature=op.TemperatureSetpoint;
            dto.TemperatureUnit=(op.TemperatureUnit ?? "C").Trim().ToUpperInvariant();
            if (dto.TemperatureUnit is not ("C" or "F")) throw new ValidationException("TemperatureUnit", "Đơn vị nhiệt độ phải là C hoặc F.");
            if (dto.TempTolerance < 0) throw new ValidationException("TempTolerance", "Dung sai nhiệt độ không được âm.");
        }
        if (dto.IsReeferPowerRequired && !dto.TargetTemperature.HasValue)
            throw new ValidationException("TargetTemperature", "Nhập nhiệt độ theo chỉ dẫn hàng lạnh.");
        if (op.CargoType == "Reefer" && !op.TemperatureSetpoint.HasValue)
            throw new ValidationException("TemperatureSetpoint", "Nhập nhiệt độ bảo quản hàng lạnh. Thông tin này vẫn cần cho Booking Nhận container và khi không yêu cầu cấp điện tại bãi.");
        if (op.CargoType == "DangerousGoods" && (string.IsNullOrWhiteSpace(op.UnNumber) || string.IsNullOrWhiteSpace(op.DgClass)))
            throw new ValidationException("DangerousGoods", "Bổ sung mã UN và phân lớp hàng nguy hiểm; không dùng giá trị mặc định.");
        if (string.IsNullOrWhiteSpace(dto.BookingCode)) dto.BookingCode="BK-"+Guid.NewGuid().ToString("N").ToUpperInvariant();
        // Names/plates are resolved from the selected fleet, never accepted as overrides.
        dto.DriverName=null; dto.VehiclePlate=null; dto.VehicleId=dto.TruckId;
        return source.GrossWeightKg.Value;
    }

    private const string CargoSelect = """
        SELECT c.id AS "ContainerId",d.id AS "DeclarationId",NULL::uuid AS "ContainerVisitId",NULL::text AS "YardCode",
          c.container_no AS "ContainerNumber",ct.code AS "ContainerTypeCode",d.load_status AS "LoadStatus",
          d.cargo_type::text AS "CargoType",d.seal_number AS "SealNumber",d.customer_name AS "CustomerName",
          (SELECT company_name FROM carriers WHERE id=d.carrier_id) AS "CarrierName",
          d.gross_weight_kg AS "GrossWeightKg",d.required_power_kw AS "RequiredPowerKw"
        FROM container_declarations d JOIN containers c ON c.id=d.container_id
        JOIN container_types ct ON ct.id=c.container_type_id
        """;

    public async Task<IReadOnlyList<BookingCargoSource>> GetBookingSourcesAsync(Guid carrierId, string? search, CancellationToken token, string bookingType = "Dropoff",int limit=30)
    {
        if(limit is <1 or >100)throw new ValidationException("Limit","Danh sách container tối đa 100 dòng mỗi lần tải.");
        var number = (search ?? "").Trim().ToUpperInvariant();
        if (number.Length is > 0 and < 3) return Array.Empty<BookingCargoSource>();
        if (number.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(number, "^[A-Z0-9]{3,20}$"))
            throw new ValidationException("Search", "Tìm theo chữ và số của container, tối thiểu 3 ký tự.");
        if (bookingType == "Pickup")
            return await _context.Database.SqlQueryRaw<BookingCargoSource>("""
                SELECT c.id AS "ContainerId",'00000000-0000-0000-0000-000000000000'::uuid AS "DeclarationId",
                  v.id AS "ContainerVisitId",v.pickup_code AS "YardCode",c.container_no AS "ContainerNumber",ct.code AS "ContainerTypeCode",
                  'unknown'::text AS "LoadStatus",NULL::text AS "CargoType",NULL::text AS "SealNumber",
                  NULL::text AS "CustomerName",NULL::text AS "CarrierName",NULL::numeric AS "GrossWeightKg",NULL::numeric AS "RequiredPowerKw"
                FROM containers c JOIN container_types ct ON ct.id=c.container_type_id
                JOIN container_visits v ON v.container_id=c.id JOIN container_declarations d ON d.id=v.declaration_id
                WHERE c.container_no ILIKE {1} AND v.visit_status IN ('planned','active')
                  AND (d.carrier_id={0} OR EXISTS(SELECT 1 FROM container_visit_access a WHERE a.container_visit_id=v.id AND a.carrier_id={0}))
                  AND EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
                    WHERE bc.container_visit_id=v.id AND yard_booking_direction(b.id,bc.container_id)='dropoff' AND b.status::text NOT IN ('canceled','rejected','expired'))
                  AND NOT EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
                    WHERE bc.container_visit_id=v.id AND yard_booking_direction(b.id,bc.container_id)='pickup' AND b.status::text NOT IN ('canceled','rejected','expired'))
                  AND NOT EXISTS(SELECT 1 FROM bookings b WHERE b.status::text NOT IN ('canceled','rejected','expired','completed') AND (
                    EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.booking_id=b.id AND bc.container_id=c.id
                      AND bc.container_visit_id IS NULL AND yard_booking_direction(b.id,c.id)='pickup')
                    OR EXISTS(SELECT 1 FROM booking_operations o WHERE o.booking_id=b.id AND o.operation_type='Import'
                      AND (o.container_id=c.id OR upper(trim(o.container_number))=upper(c.container_no))
                      AND NOT EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.booking_id=b.id
                        AND bc.container_id=c.id AND bc.container_visit_id IS NOT NULL))))
                ORDER BY c.container_no,c.id,v.id LIMIT {2}
                """, carrierId, "%" + number + "%",limit).ToListAsync(token);
        if (bookingType != "Dropoff") throw new ValidationException("BookingType", "Chọn loại lịch Giao hoặc Nhận.");
        return await _context.Database.SqlQueryRaw<BookingCargoSource>(CargoSelect + """

            WHERE d.carrier_id={0} AND c.container_no ILIKE {1} AND d.load_status='full'
            AND NOT EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.declaration_id=d.id)
            AND NOT EXISTS(SELECT 1 FROM bookings b WHERE b.status::text NOT IN ('canceled','rejected','expired','completed') AND (
              EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.booking_id=b.id AND bc.container_id=c.id)
              OR EXISTS(SELECT 1 FROM booking_operations o WHERE o.booking_id=b.id
                AND (o.container_id=c.id OR upper(trim(o.container_number))=upper(c.container_no)))))
            AND NOT EXISTS(SELECT 1 FROM container_visits v WHERE v.container_id=c.id AND v.visit_status IN ('planned','active')
              AND (v.started_at IS NOT NULL OR EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
                WHERE bc.container_visit_id=v.id AND b.status::text NOT IN ('canceled','rejected','expired'))))
            ORDER BY d.created_at DESC,d.id LIMIT {2}
            """, carrierId, "%" + number + "%",limit).ToListAsync(token);
    }

    public async Task<BookingPlan> PlanBookingAsync(Guid carrierId, BookingPlanningRequest request, CancellationToken token)
    {
        BookingFleetPolicy.ValidateWindow(request.AppointmentStart, request.AppointmentEnd);
        var legs=request.Legs is not {Count:>0}?new List<BookingPlanningLeg>{request}:request.Legs.OrderBy(leg=>leg.BookingType=="Dropoff"?0:1).ToList();
        if(request.BookingId.HasValue)
        {
            var storedLegs=await _context.Database.SqlQueryRaw<BookingPlanningLeg>("""
                SELECT bc.container_id AS "ContainerId",bc.declaration_id AS "DeclarationId",NULL::text AS "YardCode",
                  CASE yard_booking_direction(b.id,bc.container_id) WHEN 'pickup' THEN 'Pickup' ELSE 'Dropoff' END AS "BookingType"
                FROM bookings b JOIN booking_containers bc ON bc.booking_id=b.id
                WHERE b.id={0} AND b.carrier_id={1} AND b.is_combined
                ORDER BY CASE yard_booking_direction(b.id,bc.container_id) WHEN 'dropoff' THEN 0 ELSE 1 END
                """,request.BookingId.Value,carrierId).ToListAsync(token);
            if(storedLegs.Count>0)legs=storedLegs;
        }
        if(legs.Count>1&&(legs.Count!=2||legs.Count(leg=>leg.BookingType=="Dropoff")!=1||legs.Count(leg=>leg.BookingType=="Pickup")!=1||legs.Select(leg=>leg.ContainerId).Distinct().Count()!=2))
            throw new ValidationException("Operations","Chuyến hai chiều cần một lượt Giao và một lượt Nhận của hai container khác nhau.");
        var resolved=new List<(BookingPlanningRequest Request,BookingCargoSource Source)>();
        foreach(var leg in legs)
        {
            var legRequest=new BookingPlanningRequest {BookingId=request.BookingId,ContainerId=leg.ContainerId,DeclarationId=leg.DeclarationId,YardCode=leg.YardCode,
                BookingType=leg.BookingType,AppointmentStart=request.AppointmentStart,AppointmentEnd=request.AppointmentEnd};
            resolved.Add((legRequest,await ResolveBookingSourceAsync(carrierId,legRequest,token,forPlanning:true)));
        }
        var fleet = await LoadBookingFleetAsync(carrierId, request.AppointmentStart, request.AppointmentEnd,
            request.BookingId, token);
        var schedule=await AssessSchedulesAsync(carrierId,resolved,fleet,token);
        return new BookingPlan { Source = resolved[0].Source, Sources=resolved.Select(leg=>leg.Source).ToList(),Fleet = fleet, Schedule = schedule };
    }

    private async Task<BookingCargoSource> ResolveBookingSourceAsync(Guid carrierId, BookingPlanningRequest request, CancellationToken token, bool forPlanning = false)
    {
        List<BookingCargoSource> rows;
        if (request.BookingId.HasValue)
        {
            rows = await _context.Database.SqlQueryRaw<BookingCargoSource>(CargoSelect.Replace("NULL::uuid", "bc.container_visit_id") + """

                JOIN booking_containers bc ON bc.declaration_id=d.id AND bc.container_id=c.id
                JOIN bookings b ON b.id=bc.booking_id
                WHERE b.id={0} AND b.carrier_id={1} AND c.id={2}
                """, request.BookingId.Value, carrierId, request.ContainerId).ToListAsync(token);
        }
        else if (request.BookingType == "Dropoff")
        {
            rows = await _context.Database.SqlQueryRaw<BookingCargoSource>(CargoSelect + """

                WHERE d.id={0} AND c.id={1} AND d.carrier_id={2}
                AND NOT EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.declaration_id=d.id)
                """, request.DeclarationId ?? Guid.Empty, request.ContainerId, carrierId).ToListAsync(token);
        }
        else if (request.BookingType == "Pickup")
        {
            var code = NormalizeYardCode(request.YardCode);
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^TC-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}$"))
                throw new ValidationException("YardCode", "Nhập đúng mã nhận container của lượt bãi.");
            rows = await _context.Database.SqlQueryRaw<BookingCargoSource>(CargoSelect.Replace("NULL::uuid", "v.id")
                .Replace("NULL::text AS \"YardCode\"", "v.pickup_code AS \"YardCode\"") + """

                JOIN container_visits v ON v.declaration_id=d.id AND v.container_id=c.id
                WHERE c.id={0} AND v.pickup_code={1} AND v.visit_status IN ('planned','active')
                AND EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
                  WHERE bc.container_visit_id=v.id AND yard_booking_direction(b.id,bc.container_id)='dropoff'
                  AND b.status::text NOT IN ('canceled','rejected','expired') AND b.appointment_end<={2})
                AND NOT EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
                  WHERE bc.container_visit_id=v.id AND yard_booking_direction(b.id,bc.container_id)='pickup'
                  AND b.status::text NOT IN ('canceled','rejected','expired'))
                """, request.ContainerId, code, forPlanning ? DateTime.MaxValue : request.AppointmentStart).ToListAsync(token);
        }
        else throw new ValidationException("BookingType", "Chọn Giao container hoặc Nhận container.");

        if (rows.Count != 1) throw new ValidationException("Container", "Không tìm thấy bản khai/lượt bãi hợp lệ. Kiểm tra quyền, mã nhận và thời gian giao nhận.");
        var source = rows[0];
        if (source.LoadStatus != "full") throw new ValidationException("LoadStatus", "Booking xuất/nhập hiện chỉ hỗ trợ container có hàng.");
        if (string.IsNullOrWhiteSpace(source.CargoType)) throw new ValidationException("CargoType", "Bổ sung loại hàng trong bản khai.");
        return source;
    }

    private async Task<BookingFleetOptions> LoadBookingFleetAsync(Guid carrierId, DateTime start, DateTime end,
        Guid? excludeId, CancellationToken token)
    {
        BookingFleetPolicy.ValidateWindow(start, end);
        // Live trips and an unclosed physical Gate In remain busy, even after the planned end.
        var busy = await _context.Database.SqlQueryRaw<BookingBusyFleet>("""
            SELECT b.driver_id AS "DriverId",COALESCE(b.truck_id,b.vehicle_id,bt.id) AS "TruckId" FROM bookings b
            LEFT JOIN trucks bt ON b.truck_id IS NULL AND b.vehicle_id IS NULL AND b.vehicle_plate IS NOT NULL
              AND regexp_replace(upper(bt.plate_number),'[^A-Z0-9]','','g')=regexp_replace(upper(b.vehicle_plate),'[^A-Z0-9]','','g')
            WHERE b.id<>{2} AND (
              (b.status::text NOT IN ('canceled','rejected','expired','completed') AND b.appointment_start<{1} AND b.appointment_end>{0})
              OR b.status::text='checked_in'
              OR (b.status::text NOT IN ('canceled','rejected','expired','completed') AND b.trip_status IN ('Arrived','Processing','PartiallyCompleted'))
              OR EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type::text='gate_in' AND g.status::text='completed'
                AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=b.id AND x.gate_type::text='gate_out'
                  AND x.status::text='completed' AND x.checked_at>=g.checked_at)))
            UNION ALL
            SELECT d.id AS "DriverId",COALESCE(t.truck_id,tt.id) AS "TruckId" FROM transport_trips t
            LEFT JOIN drivers d ON d.user_id=t.driver_id
            LEFT JOIN trucks tt ON t.truck_id IS NULL AND t.truck_plate IS NOT NULL
              AND regexp_replace(upper(tt.plate_number),'[^A-Z0-9]','','g')=regexp_replace(upper(t.truck_plate),'[^A-Z0-9]','','g')
            WHERE (t.booking_id IS NULL OR t.booking_id<>{2}) AND t.status NOT IN ('Completed','Cancelled')
              AND (t.status NOT IN ('Assigned','Acknowledged') OR t.appointment_start IS NULL OR t.appointment_end IS NULL
                OR (t.appointment_start<{1} AND t.appointment_end>{0}))
            """, start, end, excludeId ?? Guid.Empty).ToListAsync(token);
        var driverIds = busy.Where(b => b.DriverId.HasValue).Select(b => b.DriverId!.Value).ToHashSet();
        var truckIds = busy.Where(b => b.TruckId.HasValue).Select(b => b.TruckId!.Value).ToHashSet();
        var trucks = await _context.Set<Vehicle.Domain.Entities.Vehicle>().AsNoTracking()
            .Where(t => t.CarrierId == carrierId).ToListAsync(token);
        var drivers = await _context.Set<Driver.Domain.Entities.Driver>().AsNoTracking()
            .Where(d => d.CarrierId == carrierId).ToListAsync(token);
        var activeTrucks = trucks.Where(t=>t.Status == Vehicle.Domain.Enums.TruckStatus.active).ToList();
        var freeTrucks = activeTrucks.Where(t=>!truckIds.Contains(t.Id)).ToList();
        var eligibleTrucks = freeTrucks.Select(VehicleDetails).ToList();
        var eligibleDrivers = drivers.Where(d => !driverIds.Contains(d.Id) && !string.IsNullOrWhiteSpace(d.LicenseNumber)
            && BookingFleetPolicy.IsAvailableDriver(d.Status.ToString(), d.LicenseExpiryDate, end))
            .Select(DriverDetails).ToList();
        foreach(var truck in eligibleTrucks) SignFleetDocuments(truck,null);
        foreach(var driver in eligibleDrivers) SignFleetDocuments(null,driver);
        var result = BookingFleetPolicy.Rank(eligibleTrucks, eligibleDrivers);
        result.CompanyTruckCount=trucks.Count; result.CompanyDriverCount=drivers.Count;
        if (trucks.Count==0) result.UnavailableReasons.Add("Công ty chưa có xe trong danh mục.");
        if (activeTrucks.Count<trucks.Count) result.UnavailableReasons.Add($"{trucks.Count-activeTrucks.Count} xe ngừng hoạt động hoặc bảo trì.");
        if (freeTrucks.Count<activeTrucks.Count) result.UnavailableReasons.Add($"{activeTrucks.Count-freeTrucks.Count} xe đang có lịch trùng hoặc chuyến chưa kết thúc.");
        if (eligibleDrivers.Count<drivers.Count) result.UnavailableReasons.Add($"{drivers.Count-eligibleDrivers.Count} tài xế đang bận, không ở trạng thái sẵn sàng hoặc thiếu/hết hạn hồ sơ bằng lái.");
        if (drivers.Count==0) result.UnavailableReasons.Add("Công ty chưa có tài xế trong danh mục.");
        return result;
    }

    private async Task LockBookingFleetAsync(Guid? truckId, Guid? driverId, CancellationToken token)
    {
        // Consistent ordering across create/update/assign. Locks last until transaction commit.
        if (truckId.HasValue && truckId != Guid.Empty)
            await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM trucks WHERE id={0} FOR UPDATE", truckId.Value).ToListAsync(token);
        if (driverId.HasValue && driverId != Guid.Empty)
            await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM drivers WHERE id={0} FOR UPDATE", driverId.Value).ToListAsync(token);
    }

    private async Task EnsureBookingFleetAsync(Guid carrierId, Guid? truckId, Guid? driverId, DateTime start, DateTime end,
        Guid? bookingId, CancellationToken token)
    {
        if (!truckId.HasValue && !driverId.HasValue) return;
        var available = await LoadBookingFleetAsync(carrierId, start, end, bookingId, token);
        if (truckId.HasValue && !available.Trucks.Any(t => t.Id == truckId))
            throw new ValidationException("TruckId", "Xe đang bận, không hoạt động hoặc không thuộc công ty. Chọn lại xe hoặc giờ hẹn.");
        if (driverId.HasValue && !available.Drivers.Any(d => d.Id == driverId))
            throw new ValidationException("DriverId", "Tài xế đang bận hoặc không đủ điều kiện trong khung giờ hẹn. Chọn lại tài xế.");
    }


}

public sealed class BookingBusyFleet
{
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }
}


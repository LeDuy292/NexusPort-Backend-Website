using System.Text.Json.Nodes;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Services;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceDraftService
{
    private static (DateTime Start, DateTime End)? TripWindow(ServiceDeliveryTrip trip)
    {
        if (!DateTimeOffset.TryParse(trip.AppointmentStart, out var start)
            || !DateTimeOffset.TryParse(trip.AppointmentEnd, out var end)
            || end <= start || end - start > TimeSpan.FromHours(24)) return null;
        return (start.UtcDateTime, end.UtcDateTime);
    }

    private static bool Overlaps(ServiceDeliveryTrip a, ServiceDeliveryTrip b)
        => TripWindow(a) is { } x && TripWindow(b) is { } y && x.Start < y.End && y.Start < x.End;

    private async Task<BookingFleetOptions> Fleet(Guid company, ServiceDeliveryTrip trip, CancellationToken ct)
    {
        Require(fleetAvailability != null, "Chức năng lựa chọn đội xe chưa được cấu hình.", 503, "SERVICE_FLEET_UNAVAILABLE");
        var window = TripWindow(trip);
        Require(window != null, "Chọn giờ bắt đầu và kết thúc chuyến hợp lệ, tối đa 24 giờ.");
        return await fleetAvailability!.GetFleetAsync(company, window!.Value.Start, window.Value.End, ct);
    }

    private static void CheckTripScope(ServiceDraftInput input)
    {
        var ids = input.Lines.Select(l => l.SourceContainerId).ToHashSet();
        Require(input.Delivery.Trips.All(t => ids.Contains(t.SourceContainerId)), "Chuyến giao nhận phải thuộc container đã chọn trong hồ sơ.");
    }

    // Read-only proposal. Dispatcher/Booking owns actual transport reservations.
    public async Task<object> RecommendServiceFleet(ServiceDraftInput request, CancellationToken ct)
    {
        var company = await Company(ct);
        var input = ServiceDraftRules.Normalize(request);
        Require(input.Delivery.TransportMode == "TRUCK", "Chỉ lựa chọn đội xe cho giao nhận đường bộ.");
        CheckTripScope(input);
        Require(input.Lines.Length > 0 && input.Delivery.Trips.Length == input.Lines.Length, "Khai báo một chuyến cho mỗi container.");
        await Snapshots(company, input, ct);
        var selected = new List<ServiceDeliveryTrip>();
        var results = new List<object>();
        foreach (var trip in input.Delivery.Trips.OrderBy(t => t.AppointmentStart).ThenBy(t => t.SourceContainerId))
        {
            var available = await Fleet(company, trip, ct);
            var overlapping = selected.Where(t => Overlaps(t, trip)).ToArray();
            var ranked = BookingFleetPolicy.Rank(
                available.Trucks.Where(t => !overlapping.Any(x => x.TruckId == t.Id)),
                available.Drivers.Where(d => !overlapping.Any(x => x.DriverId == d.Id)));
            var truck = ranked.Trucks.FirstOrDefault(t => t.Id == ranked.RecommendedTruckId)
                ?? ranked.Trucks.FirstOrDefault();
            var driver = ranked.Drivers.FirstOrDefault(d => d.Id == ranked.RecommendedDriverId)
                ?? ranked.Drivers.FirstOrDefault(d => d.Id == truck?.DriverId)
                ?? ranked.Drivers.FirstOrDefault();
            selected.Add(trip with { TruckId = truck?.Id, DriverId = driver?.Id });
            results.Add(new { trip.SourceContainerId, trip.AppointmentStart, trip.AppointmentEnd,
                Trucks = ranked.Trucks, Drivers = ranked.Drivers, RecommendedTruckId = truck?.Id,
                RecommendedDriverId = driver?.Id, Message = truck != null && driver != null ? ranked.Message : "Không đủ xe hoặc tài xế rảnh cho chuyến này. Hãy đổi khung giờ.",
                available.UnavailableReasons });
        }
        return new { items = results, reservationCreated = false };
    }

    private static ServiceTripVehicle VehicleSnapshot(AvailableTruckDto t)
        => new(t.Id, t.PlateNumber, t.VehicleType, t.Status, t.CurrentLocation, t.RfidTag, t.Description);
    private static ServiceTripDriver DriverSnapshot(AvailableDriverDto d)
        => new(d.Id, d.FullName, d.Phone, d.LicenseNumber, d.Status, d.LicenseExpiryDate, d.IdCardNumber, d.IdCardExpiryDate);

    private async Task<ServiceDraftInput> CaptureFleet(Guid company, ServiceDraftInput input, CancellationToken ct)
    {
        CheckTripScope(input);
        var captured = new List<ServiceDeliveryTrip>();
        foreach (var trip in input.Delivery.Trips)
        {
            var next = trip with { Vehicle = null, Driver = null };
            if (trip.TruckId.HasValue || trip.DriverId.HasValue)
            {
                var available = await Fleet(company, trip, ct);
                var truck = available.Trucks.FirstOrDefault(t => t.Id == trip.TruckId);
                var driver = available.Drivers.FirstOrDefault(d => d.Id == trip.DriverId);
                Require(!trip.TruckId.HasValue || truck != null, "Xe đã bận hoặc không thuộc đội xe đang hoạt động của công ty. Chọn lại xe.", 409, "SERVICE_TRUCK_UNAVAILABLE");
                Require(!trip.DriverId.HasValue || driver != null, "Tài xế đã bận hoặc hồ sơ không còn hợp lệ. Chọn lại tài xế.", 409, "SERVICE_DRIVER_UNAVAILABLE");
                Require(!captured.Any(t => Overlaps(t, trip) && ((trip.TruckId.HasValue && t.TruckId == trip.TruckId)
                    || (trip.DriverId.HasValue && t.DriverId == trip.DriverId))), "Xe hoặc tài xế bị trùng giờ giữa các container.");
                next = next with { Vehicle = truck == null ? null : VehicleSnapshot(truck), Driver = driver == null ? null : DriverSnapshot(driver) };
            }
            captured.Add(next);
        }
        return input with { Delivery = input.Delivery with { Trips = captured.ToArray(), TruckPlate = "", DriverName = "", DriverPhone = "" } };
    }

    private async Task<JsonArray> FleetIssues(ServiceDraftInput input, Guid company, JsonObject source, CancellationToken ct)
    {
        var issues = new JsonArray();
        if (input.Delivery.TransportMode != "TRUCK") return issues;
        void Add(string code, string text, Guid? id = null) => issues.Add(new JsonObject {
            ["code"] = code, ["message"] = text, ["field"] = "delivery.trips", ["severity"] = "ERROR", ["sourceContainerId"] = id?.ToString() });
        foreach (var line in input.Lines)
        {
            var trip = input.Delivery.Trips.FirstOrDefault(t => t.SourceContainerId == line.SourceContainerId);
            if (trip == null || trip.TruckId == null || trip.DriverId == null || TripWindow(trip) == null)
            { Add("TRIP_REQUIRED", "Bổ sung lịch, xe và tài xế cho từng container.", line.SourceContainerId); continue; }
            var window = TripWindow(trip)!.Value;
            if (window.Start < DateTime.UtcNow || (At(input.PlannedAt) is { } planned && window.Start < planned.UtcDateTime))
                Add("TRIP_TIME_INVALID", "Giờ chuyến phải từ thời gian dự kiến thực hiện và chưa qua.", line.SourceContainerId);
            var endDate = TimeZoneInfo.ConvertTimeFromUtc(window.End, PortZone).ToString("yyyy-MM-dd");
            if ((input.Delivery.OrderValidUntil != null && string.CompareOrdinal(endDate, input.Delivery.OrderValidUntil) > 0)
                || (input.Flow == "IMPORT" && input.Source.ValidUntil != null && string.CompareOrdinal(endDate, input.Source.ValidUntil) > 0)
                || (input.Flow == "EXPORT" && ((At(S(source, "cyCutoff")) is { } cutoff && window.End > cutoff.UtcDateTime)
                    || (At(S(source, "receiveFrom")) is { } receiving && window.Start < receiving.UtcDateTime))))
                Add("TRIP_OUTSIDE_SOURCE_WINDOW", "Lịch chuyến vượt thời gian tiếp nhận hoặc hạn lệnh. Chọn lại khung giờ.", line.SourceContainerId);
            if (input.Delivery.Trips.Any(t => t.SourceContainerId != trip.SourceContainerId && Overlaps(t, trip)
                && (t.TruckId == trip.TruckId || t.DriverId == trip.DriverId)))
                Add("TRIP_FLEET_OVERLAP", "Xe hoặc tài xế trùng giờ giữa các container.", line.SourceContainerId);
            var available = await Fleet(company, trip, ct);
            var truck = available.Trucks.FirstOrDefault(t => t.Id == trip.TruckId);
            var driver = available.Drivers.FirstOrDefault(d => d.Id == trip.DriverId);
            if (truck == null || driver == null) Add("TRIP_FLEET_UNAVAILABLE", "Xe hoặc tài xế không còn rảnh/hợp lệ. Mở chỉnh sửa và chọn lại.", line.SourceContainerId);
            else if (trip.Vehicle != VehicleSnapshot(truck) || trip.Driver != DriverSnapshot(driver))
                Add("TRIP_PROFILE_CHANGED", "Thông tin xe hoặc tài xế đã thay đổi. Mở chỉnh sửa và lưu lại hồ sơ.", line.SourceContainerId);
            if (driver != null && (string.IsNullOrWhiteSpace(driver.Phone) || driver.Phone.Count(char.IsDigit) < 7))
                Add("TRIP_DRIVER_PHONE_REQUIRED", "Tài xế chưa có số điện thoại hợp lệ. Cập nhật hồ sơ tài xế hoặc chọn người khác.", line.SourceContainerId);
        }
        return issues;
    }
}

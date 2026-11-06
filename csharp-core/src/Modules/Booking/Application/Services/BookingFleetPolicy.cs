using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public static class BookingFleetPolicy
{
    public static bool IsAvailableDriver(string status, DateTime? licenseExpiry, DateTime end) =>
        (status is "active" or "vehicle_received" or "transport_completed")
        && (!licenseExpiry.HasValue || licenseExpiry.Value.Date >= end.Date);

    public static void ValidateWindow(DateTime start, DateTime end)
    {
        if (start == default || end == default || start >= end || end - start > TimeSpan.FromHours(24))
            throw new ValidationException("Appointment", "Chọn khung giờ hợp lệ, tối đa 24 giờ.");
    }

    public static BookingFleetOptions Rank(IEnumerable<AvailableTruckDto> trucks, IEnumerable<AvailableDriverDto> drivers)
    {
        var result = new BookingFleetOptions { Drivers = drivers.OrderBy(d => d.FullName).ThenBy(d => d.Id).ToList() };
        // Booking currently selects by availability, without enforcing vehicle payload.
        result.Trucks = trucks.OrderByDescending(t => result.Drivers.Any(d => d.Id == t.DriverId))
            .ThenBy(t => t.PlateNumber).ToList();
        var truck = result.Trucks.FirstOrDefault();
        var driver = result.Drivers.FirstOrDefault(d => d.Id == truck?.DriverId) ?? result.Drivers.FirstOrDefault();
        if (truck != null && driver != null) {
            result.RecommendedTruckId = truck.Id; result.RecommendedDriverId = driver.Id;
            result.Message = "Xe và tài xế không bận trong khung giờ đã chọn; ưu tiên tài xế phụ trách xe.";
        } else result.Message = "Chưa có đủ xe và tài xế rảnh. Hãy đổi giờ hoặc bổ sung dữ liệu đội xe.";
        return result;
    }
}

using NexusPort.Modules.Booking.Domain.Entities;
using NexusPort.Modules.Container.Domain.Entities;
using NexusPort.Modules.Driver.Domain.Entities;
using NexusPort.Modules.Vehicle.Domain.Entities;

namespace NexusPort.Modules.Gate.Domain.Rules;

/// <summary>
/// Ngữ cảnh xác thực chứa toàn bộ thông tin phương tiện, tài xế, container, booking và thời gian
/// để cung cấp cho các Gate Rules đánh giá.
/// </summary>
public class GateValidationContext
{
    public string GateType { get; set; } = "GateIn"; // "GateIn" hoặc "GateOut"
    public string GateCode { get; set; } = string.Empty;
    public string? LaneCode { get; set; }
    public DateTime VerificationTime { get; set; } = DateTime.UtcNow;

    // Thông tin Phương tiện (Vehicle) & Cảm biến (Sensor / RFID)
    public string VehiclePlate { get; set; } = string.Empty;
    public string? RfidTag { get; set; }
    public bool VehicleDetected { get; set; } = true;
    public Vehicle.Domain.Entities.Vehicle? Vehicle { get; set; }

    // Thông tin Tài xế (Driver)
    public string? DriverName { get; set; }
    public string? DriverLicense { get; set; }
    public Driver.Domain.Entities.Driver? Driver { get; set; }

    // Thông tin Container
    public string? ContainerNumber { get; set; }
    public Container.Domain.Entities.Container? Container { get; set; }

    // Thông tin Booking
    public string? BookingNumber { get; set; }
    public Booking.Domain.Entities.Booking? Booking { get; set; }

    // Dữ liệu mở rộng tuỳ chọn truyền giữa các rule
    public IDictionary<string, object> Items { get; set; } = new Dictionary<string, object>();
}

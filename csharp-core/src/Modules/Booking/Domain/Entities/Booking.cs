using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Booking.Domain.Entities;

public class Booking : BaseEntity, IAggregateRoot
{
    public string BookingNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    // Associated Vehicle & Driver
    public string? VehiclePlate { get; set; }
    public Guid? VehicleId { get; set; }
    public string? DriverName { get; set; }
    public Guid? DriverId { get; set; }

    // Time validity window
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }

    // Gate operation type (e.g. GateIn, GateOut, Delivery, Receiving)
    public string? GateType { get; set; } = "GateIn";

    public Booking() { }

    public Booking(string val, string? description = null)
    {
        BookingNumber = val;
        Description = description;
    }
}

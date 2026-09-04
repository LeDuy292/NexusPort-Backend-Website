namespace NexusPort.Modules.Booking.Application.DTOs;

public class BookingDto
{
    public Guid Id { get; set; }
    public string BookingNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? VehiclePlate { get; set; }
    public Guid? VehicleId { get; set; }
    public string? DriverName { get; set; }
    public Guid? DriverId { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? GateType { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateBookingDto
{
    public string BookingNumber { get; set; } = string.Empty;
    public string? Status { get; set; } = "Active";
    public string? Description { get; set; }
    public string? VehiclePlate { get; set; }
    public Guid? VehicleId { get; set; }
    public string? DriverName { get; set; }
    public Guid? DriverId { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? GateType { get; set; } = "GateIn";
}

namespace NexusPort.Modules.Booking.Application.DTOs;

public sealed class FleetScheduleDto
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public List<FleetScheduleItem> Items { get; set; } = new();
    public List<FleetResourceDto> Drivers { get; set; } = new();
    public List<FleetResourceDto> Vehicles { get; set; } = new();
}

public sealed class FleetScheduleItem
{
    public Guid Id { get; set; }
    public Guid? BookingId { get; set; }
    public string Source { get; set; } = "Booking";
    public string Code { get; set; } = "";
    public string Status { get; set; } = "";
    public string TripStatus { get; set; } = "";
    public string BookingType { get; set; } = "";
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? VehicleId { get; set; }
    public string? DriverName { get; set; }
    public string? VehiclePlate { get; set; }
    public bool CanReassign { get; set; }
}

public sealed class FleetResourceDto
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public string? Phone { get; set; }
    public string Status { get; set; } = "";
    public bool IsAvailable { get; set; }
}

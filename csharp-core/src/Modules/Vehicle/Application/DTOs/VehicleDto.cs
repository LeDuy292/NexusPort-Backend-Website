namespace NexusPort.Modules.Vehicle.Application.DTOs;

public class VehicleDto
{
    public Guid Id { get; set; }
    public Guid CarrierId { get; set; }
    public Guid? DriverId { get; set; }
    public string? CarrierName { get; set; }
    public string? DriverName { get; set; }
    public string PlateNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? VehicleType { get; set; }
    public string? Description { get; set; }
    public string? RegistrationImageUrl { get; set; }
    public string? PhotoUrl { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateVehicleDto
{
    public string PlateNumber { get; set; } = string.Empty;
    public string? VehicleType { get; set; }
    public string? Description { get; set; }
    public string? RegistrationImageUrl { get; set; }
    public string? PhotoUrl { get; set; }
}

public class UpdateVehicleDto
{
    public string PlateNumber { get; set; } = string.Empty;
    public string? VehicleType { get; set; }
    public string? Description { get; set; }
    public string? RegistrationImageUrl { get; set; }
    public string? PhotoUrl { get; set; }
}

public class AssignDriverDto
{
    public Guid? DriverId { get; set; }
}

public class VehicleFilterDto
{
    public Guid? CarrierId { get; set; }
    public string? Status { get; set; }
    public string? SearchTerm { get; set; }
}

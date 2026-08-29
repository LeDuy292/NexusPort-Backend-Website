namespace NexusPort.Modules.Vehicle.Application.DTOs;

public class VehicleDto
{
    public Guid Id { get; set; }
    public string PlateNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateVehicleDto
{
    public string PlateNumber { get; set; } = string.Empty;
    public string? Description { get; set; }
}

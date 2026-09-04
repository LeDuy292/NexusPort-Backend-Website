using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Vehicle.Domain.Entities;

public class Vehicle : BaseEntity, IAggregateRoot
{
    public Guid CarrierId { get; set; }
    public string PlateNumber { get; set; } = string.Empty;
    public string? RfidTag { get; set; }
    public string VehicleType { get; set; } = "Truck";
    public string Status { get; set; } = "active";
    public string? Description { get; set; }

    public Vehicle() { }

    public Vehicle(Guid carrierId, string plateNumber, string vehicleType = "Truck", string status = "active", string? rfidTag = null)
    {
        CarrierId = carrierId;
        PlateNumber = plateNumber;
        VehicleType = vehicleType;
        Status = status;
        RfidTag = rfidTag;
    }
}

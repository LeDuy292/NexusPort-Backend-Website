using NexusPort.Shared.Kernel;
using NexusPort.Modules.Vehicle.Domain.Enums;

namespace NexusPort.Modules.Vehicle.Domain.Entities;

public class Vehicle : BaseEntity, IAggregateRoot
{
    public Guid CarrierId { get; set; }
    public Guid? DriverId { get; set; }
    public string PlateNumber { get; set; } = string.Empty;
    public string? RfidTag { get; set; }
    public string VehicleType { get; set; } = "Truck";
    public TruckStatus Status { get; set; } = TruckStatus.active;
    public string? Description { get; set; }

    public Vehicle() { }

    public Vehicle(Guid carrierId, string plateNumber, string vehicleType = "Truck", TruckStatus status = TruckStatus.active, string? rfidTag = null, Guid? driverId = null)
    {
        CarrierId = carrierId;
        DriverId = driverId;
        PlateNumber = plateNumber;
        VehicleType = vehicleType;
        Status = status;
        RfidTag = rfidTag;
    }
}

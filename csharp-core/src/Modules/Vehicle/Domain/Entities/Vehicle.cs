using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Vehicle.Domain.Entities;

public class Vehicle : BaseEntity, IAggregateRoot
{
    public string PlateNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public Vehicle() { }

    public Vehicle(string val, string? description = null)
    {
        PlateNumber = val;
        Description = description;
    }
}

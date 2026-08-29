using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Vessel.Domain.Entities;

public class Vessel : BaseEntity, IAggregateRoot
{
    public string VesselName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public Vessel() { }

    public Vessel(string val, string? description = null)
    {
        VesselName = val;
        Description = description;
    }
}

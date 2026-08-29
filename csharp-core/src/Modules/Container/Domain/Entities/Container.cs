using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Container.Domain.Entities;

public class Container : BaseEntity, IAggregateRoot
{
    public string ContainerNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public Container() { }

    public Container(string val, string? description = null)
    {
        ContainerNumber = val;
        Description = description;
    }
}

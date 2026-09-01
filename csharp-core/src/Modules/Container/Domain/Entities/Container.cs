using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Container.Domain.Entities;

public class Container : BaseEntity, IAggregateRoot
{
    public Guid? CarrierId { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string SealNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "expected";
    public string CargoType { get; set; } = "general";
    public string? Description { get; set; }

    public Container() { }

    public Container(string containerNumber, string status = "expected", Guid? carrierId = null)
    {
        ContainerNumber = containerNumber;
        Status = status;
        CarrierId = carrierId;
    }
}

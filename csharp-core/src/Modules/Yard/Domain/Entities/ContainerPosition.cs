using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities;

public class ContainerPosition : BaseEntity
{
    public Guid ContainerId { get; set; }
    public Guid SlotId { get; set; }
    public Guid? PlacedBy { get; set; }
    public DateTime PlacedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RemovedAt { get; set; }
    public bool IsCurrent { get; set; } = true;

    // Navigation properties
    public YardSlot Slot { get; set; } = null!;

    public ContainerPosition() { }

    public ContainerPosition(Guid containerId, Guid slotId, Guid? placedBy = null)
    {
        ContainerId = containerId;
        SlotId = slotId;
        PlacedBy = placedBy;
        PlacedAt = DateTime.UtcNow;
        IsCurrent = true;
    }
}

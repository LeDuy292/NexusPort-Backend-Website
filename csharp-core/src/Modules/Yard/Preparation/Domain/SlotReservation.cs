using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Preparation.Domain;

public class SlotReservation : BaseEntity
{
    public Guid SlotId { get; set; }
    public Guid WorkOrderId { get; set; }
    public DateTimeOffset ReservedUntil { get; set; }

    public YardSlot? Slot { get; set; }

    public SlotReservation() { }

    public SlotReservation(Guid slotId, Guid workOrderId, DateTimeOffset reservedUntil)
    {
        Id = Guid.NewGuid();
        SlotId = slotId;
        WorkOrderId = workOrderId;
        ReservedUntil = reservedUntil;
        CreatedAt = DateTime.UtcNow;
    }
}

using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Preparation.Domain;

public class ContainerMovement : BaseEntity
{
    public string ContainerNo { get; set; } = string.Empty;
    public Guid FromSlotId { get; set; }
    public Guid ToSlotId { get; set; }
    public Guid? MovedBy { get; set; }
    public Guid? EquipmentId { get; set; }
    public DateTimeOffset MovedAt { get; set; } = DateTime.UtcNow;
    public Guid? WorkOrderId { get; set; }
    public string? Note { get; set; }

    public ContainerMovement() { }

    public ContainerMovement(string containerNo, Guid fromSlotId, Guid toSlotId, Guid? movedBy = null, Guid? equipmentId = null, Guid? workOrderId = null, string? note = null)
    {
        Id = Guid.NewGuid();
        ContainerNo = containerNo;
        FromSlotId = fromSlotId;
        ToSlotId = toSlotId;
        MovedBy = movedBy;
        EquipmentId = equipmentId;
        WorkOrderId = workOrderId;
        Note = note;
        MovedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }
}

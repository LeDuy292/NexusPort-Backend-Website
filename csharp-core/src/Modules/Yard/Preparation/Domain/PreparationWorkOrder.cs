using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Preparation.Domain;

public class PreparationWorkOrder : BaseEntity
{
    public Guid PreparationTaskId { get; set; }
    public Guid? YardTaskId { get; set; }
    public WorkOrderType Type { get; set; } = WorkOrderType.Rehandle;
    public int Sequence { get; set; } = 1;
    public string ContainerNo { get; set; } = string.Empty;
    public Guid FromSlotId { get; set; }
    public Guid ToSlotId { get; set; }
    public Guid? EquipmentId { get; set; }
    public Guid? AssigneeId { get; set; }
    public int Priority { get; set; } = 0;
    public DateTimeOffset DueAt { get; set; }
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Pending;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailReason { get; set; }
    public ChargeParty ChargeParty { get; set; } = ChargeParty.Port;
    public uint RowVersion { get; set; }
    public string? SelectionReason { get; set; }

    // Navigation
    public PreparationTask Task { get; set; } = null!;
    public YardSlot? FromSlot { get; set; }
    public YardSlot? ToSlot { get; set; }

    public PreparationWorkOrder() { }

    public PreparationWorkOrder(Guid preparationTaskId, WorkOrderType type, int sequence, string containerNo, Guid fromSlotId, Guid toSlotId, DateTimeOffset dueAt, int priority, ChargeParty chargeParty = ChargeParty.Port, string? selectionReason = null)
    {
        Id = Guid.NewGuid();
        PreparationTaskId = preparationTaskId;
        Type = type;
        Sequence = sequence;
        ContainerNo = containerNo;
        FromSlotId = fromSlotId;
        ToSlotId = toSlotId;
        DueAt = dueAt;
        Priority = priority;
        ChargeParty = chargeParty;
        SelectionReason = selectionReason;
        Status = WorkOrderStatus.Pending;
        CreatedAt = DateTime.UtcNow;
    }
}

using NexusPort.Modules.Yard.Preparation.Domain;

namespace NexusPort.Modules.Yard.Preparation.Application.DTOs;

public class WorkOrderDto
{
    public Guid Id { get; set; }
    public Guid PreparationTaskId { get; set; }
    public WorkOrderType Type { get; set; }
    public int Sequence { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public Guid FromSlotId { get; set; }
    public string FromSlotLocation { get; set; } = string.Empty;
    public Guid ToSlotId { get; set; }
    public string ToSlotLocation { get; set; } = string.Empty;
    public Guid? EquipmentId { get; set; }
    public string? EquipmentCode { get; set; }
    public Guid? AssigneeId { get; set; }
    public string? AssigneeName { get; set; }
    public int Priority { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public WorkOrderStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailReason { get; set; }
    public ChargeParty ChargeParty { get; set; }
    public string? SelectionReason { get; set; }
    public uint RowVersion { get; set; }
}

public class AssignWorkOrderRequestDto
{
    public Guid? EquipmentId { get; set; }
    public Guid? AssigneeId { get; set; }
}

public class CompleteWorkOrderRequestDto
{
    public string ScannedContainerNo { get; set; } = string.Empty;
    public Guid? ActualToSlotId { get; set; }
    public string? ChangeSlotReason { get; set; }
    public string? Note { get; set; }
}

public class FailWorkOrderRequestDto
{
    public string Reason { get; set; } = string.Empty;
}

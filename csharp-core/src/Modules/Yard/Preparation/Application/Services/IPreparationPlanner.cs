using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Modules.Yard.Preparation.Domain;

namespace NexusPort.Modules.Yard.Preparation.Application.Services;

public class ContainerStackInfo
{
    public string ContainerNo { get; set; } = string.Empty;
    public Guid SlotId { get; set; }
    public int Bay { get; set; }
    public int Row { get; set; }
    public int Tier { get; set; }
    public Guid BlockId { get; set; }
    public string BlockCode { get; set; } = string.Empty;
    public bool IsReefer { get; set; }
    public bool HasHold { get; set; }
    public DateTimeOffset? NextAppointmentAt { get; set; }
    public Guid? CarrierId { get; set; }
}

public class PlannedWorkOrderResult
{
    public int Sequence { get; set; }
    public WorkOrderType Type { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public Guid FromSlotId { get; set; }
    public Guid ToSlotId { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public ChargeParty ChargeParty { get; set; }
    public string SelectionReason { get; set; } = string.Empty;
}

public class PlanningResult
{
    public bool IsSuccessful { get; set; }
    public bool NeedsApproval { get; set; }
    public int RehandleCount { get; set; }
    public string? Reason { get; set; }
    public List<PlannedWorkOrderResult> WorkOrders { get; set; } = new();
}

public interface IPreparationPlanner
{
    PlanningResult PlanTask(
        string targetContainerNo,
        YardSlot targetSlot,
        List<ContainerStackInfo> containersInStack,
        List<YardSlot> availableSlots,
        List<SlotReservation> activeReservations,
        PreparationConfig config,
        DateTimeOffset startBy,
        Dictionary<Guid, double>? blockOccupancyRatios = null,
        ChargeParty defaultChargeParty = ChargeParty.Port);

    List<(PreparationTask Task, PlanningResult Result)> PlanBatch(
        List<PreparationTask> tasks,
        Dictionary<string, YardSlot> targetSlots,
        Dictionary<string, List<ContainerStackInfo>> stackContainersByTask,
        List<YardSlot> availableSlots,
        List<SlotReservation> activeReservations,
        PreparationConfig config,
        Dictionary<Guid, double>? blockOccupancyRatios = null);

    int CalculateDynamicLeadTime(int tier, int maxTiers = 4, int baseLeadTimeMinutes = 150);
}

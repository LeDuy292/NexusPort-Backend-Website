using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Preparation.Domain;

public class PreparationTask : BaseEntity
{
    public Guid? ServiceOrderId { get; set; }
    public Guid SlotBookingId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public DateTimeOffset TargetReadyAt { get; set; }
    public DateTimeOffset StartBy { get; set; }
    public int Priority { get; set; } = 0;
    public PreparationTaskStatus Status { get; set; } = PreparationTaskStatus.Pending;
    public int PlannedRehandleCount { get; set; } = 0;
    public int ActualRehandleCount { get; set; } = 0;
    public DateTimeOffset? ReadyAt { get; set; }
    public string? CancelReason { get; set; }
    public string? Note { get; set; }
    public uint RowVersion { get; set; }

    // Chiều làm hàng & Thông tin nghiệp vụ Chiều Xuất (Outbound Vessel Loading)
    public PreparationDirection Direction { get; set; } = PreparationDirection.Inbound;
    public Guid? VoyageId { get; set; }
    public string? VesselName { get; set; }
    public string? VoyageCode { get; set; }
    public string? StowagePosition { get; set; }
    public int? LoadingSequence { get; set; }
    public string? QuayCraneCode { get; set; }
    public string? Pod { get; set; }
    public double? VgmWeight { get; set; }
    public bool IsVgmVerified { get; set; } = false;
    public bool IsCustomsCleared { get; set; } = false;
    public bool HasHoldFlag { get; set; } = false;

    public ICollection<PreparationWorkOrder> WorkOrders { get; set; } = new List<PreparationWorkOrder>();

    public PreparationTask() { }

    public PreparationTask(Guid slotBookingId, string containerNo, DateTimeOffset targetReadyAt, DateTimeOffset startBy, int priority, Guid? serviceOrderId = null)
    {
        Id = Guid.NewGuid();
        SlotBookingId = slotBookingId;
        ContainerNo = containerNo;
        TargetReadyAt = targetReadyAt;
        StartBy = startBy;
        Priority = priority;
        ServiceOrderId = serviceOrderId;
        Status = PreparationTaskStatus.Pending;
        Direction = PreparationDirection.Inbound;
        CreatedAt = DateTime.UtcNow;
    }

    public PreparationTask(
        string containerNo,
        string vesselName,
        string voyageCode,
        DateTimeOffset targetReadyAt,
        DateTimeOffset startBy,
        int priority,
        int? loadingSequence = null,
        string? quayCraneCode = null,
        string? stowagePosition = null,
        string? pod = null,
        Guid? voyageId = null)
    {
        Id = Guid.NewGuid();
        SlotBookingId = Guid.Empty;
        ContainerNo = containerNo;
        VesselName = vesselName;
        VoyageCode = voyageCode;
        TargetReadyAt = targetReadyAt;
        StartBy = startBy;
        Priority = priority;
        LoadingSequence = loadingSequence;
        QuayCraneCode = quayCraneCode;
        StowagePosition = stowagePosition;
        Pod = pod;
        VoyageId = voyageId;
        Status = PreparationTaskStatus.Pending;
        Direction = PreparationDirection.Outbound;
        CreatedAt = DateTime.UtcNow;
    }
}

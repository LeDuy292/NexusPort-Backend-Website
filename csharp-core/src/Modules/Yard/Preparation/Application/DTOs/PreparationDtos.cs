using NexusPort.Modules.Yard.Preparation.Domain;

namespace NexusPort.Modules.Yard.Preparation.Application.DTOs;

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / Math.Max(PageSize, 1));

    public PagedResult() { }

    public PagedResult(IReadOnlyList<T> items, int totalCount, int pageNumber, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        PageNumber = pageNumber;
        PageSize = pageSize;
    }
}

public class PreparationTaskDto
{
    public Guid Id { get; set; }
    public Guid? ServiceOrderId { get; set; }
    public Guid SlotBookingId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public DateTimeOffset TargetReadyAt { get; set; }
    public DateTimeOffset StartBy { get; set; }
    public int Priority { get; set; }
    public PreparationTaskStatus Status { get; set; }
    public int PlannedRehandleCount { get; set; }
    public int ActualRehandleCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public string? CancelReason { get; set; }
    public string? Note { get; set; }
    public uint RowVersion { get; set; }

    // Chiều làm hàng & Thông tin Xuất khẩu (Outbound)
    public PreparationDirection Direction { get; set; } = PreparationDirection.Inbound;
    public string? VesselName { get; set; }
    public string? VoyageCode { get; set; }
    public string? StowagePosition { get; set; }
    public int? LoadingSequence { get; set; }
    public string? QuayCraneCode { get; set; }
    public string? Pod { get; set; }
    public double? VgmWeight { get; set; }
    public bool IsVgmVerified { get; set; }
    public bool IsCustomsCleared { get; set; }
    public bool HasHoldFlag { get; set; }

    public ContainerPositionSummaryDto? CurrentPosition { get; set; }
    public List<BlockingContainerInfoDto> BlockingContainers { get; set; } = new();
    public List<WorkOrderDto> WorkOrders { get; set; } = new();
}

public class PreparationTaskDetailDto : PreparationTaskDto
{
    public string? SealNo { get; set; }
    public ReleaseStatus ReleaseStatus { get; set; }
    public HoldFlag HoldFlag { get; set; }
    public DateTimeOffset? EdoExpiresAt { get; set; }
    public DateTimeOffset? FreeTimeUntil { get; set; }
    public string? CargoType { get; set; }
    public List<ContainerMovementDto> MovementHistory { get; set; } = new();
}

public class ContainerPositionSummaryDto
{
    public Guid SlotId { get; set; }
    public Guid BlockId { get; set; }
    public string BlockCode { get; set; } = string.Empty;
    public int Bay { get; set; }
    public int Row { get; set; }
    public int Tier { get; set; }
    public string SlotStatus { get; set; } = "empty";
    public bool HasReeferPlug { get; set; }
    public string FormattedLocation => $"{BlockCode}-{Bay:D2}-{Row:D2}-{Tier}";
}

public class BlockingContainerInfoDto
{
    public string ContainerNo { get; set; } = string.Empty;
    public Guid SlotId { get; set; }
    public int Tier { get; set; }
    public string CargoType { get; set; } = "general";
    public bool HasReeferPlug { get; set; }
    public DateTimeOffset? NextAppointmentAt { get; set; }
    public HoldFlag HoldFlag { get; set; }
}

public class CreatePreparationTaskRequestDto
{
    public Guid SlotBookingId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public DateTimeOffset TargetReadyAt { get; set; }
    public int? Priority { get; set; }
    public Guid? ServiceOrderId { get; set; }
}

public class CancelTaskRequestDto
{
    public string Reason { get; set; } = string.Empty;
}

public class ConfirmReadyRequestDto
{
    public string ContainerNo { get; set; } = string.Empty;
    public string SealNo { get; set; } = string.Empty;
}

public class PlanBatchRequestDto
{
    public DateTimeOffset? Date { get; set; }
    public List<Guid>? TaskIds { get; set; }
    public Guid? StagingBlockId { get; set; }
}

public class PlanBatchResultDto
{
    public int TotalTasks { get; set; }
    public int PlannedTasks { get; set; }
    public int NeedsApprovalTasks { get; set; }
    public int TotalRehandles { get; set; }
    public List<PreparationTaskDto> Tasks { get; set; } = new();
}

public class PreparationDashboardDto
{
    public int PendingCount { get; set; }
    public int AtRiskCount { get; set; }
    public int InProgressCount { get; set; }
    public int ReadyTodayCount { get; set; }
    public int DelayedCount { get; set; }
    public int TotalMovesToday { get; set; }
}

public class PreparationConfigDto
{
    public int LeadTimeMinutes { get; set; }
    public int MaxRehandlePerTask { get; set; }
    public Guid? StagingBlockId { get; set; }
    public int RescheduleGraceMinutes { get; set; }
    public int DelayCheckIntervalSeconds { get; set; }
    public int EstimatedMinutesPerMove { get; set; }
    public int WeightDueNear { get; set; }
    public int WeightReefer { get; set; }
    public int WeightExpiringFreeTime { get; set; }

    // Chiều Xuất
    public int OutboundLeadTimeMinutes { get; set; } = 150;
    public int ReeferMaxUnplugMinutes { get; set; } = 45;
    public int ClosingTimeGraceMinutes { get; set; } = 30;
    public OutboundMarshallingMode MarshallingMode { get; set; } = OutboundMarshallingMode.InBayHousekeeping;
}

public class CreateOutboundPreparationTaskRequestDto
{
    public string ContainerNo { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string VoyageCode { get; set; } = string.Empty;
    public DateTimeOffset TargetReadyAt { get; set; }
    public int? LoadingSequence { get; set; }
    public string? QuayCraneCode { get; set; }
    public string? StowagePosition { get; set; }
    public string? Pod { get; set; }
    public double? VgmWeight { get; set; }
    public bool IsVgmVerified { get; set; } = true;
    public bool IsCustomsCleared { get; set; } = true;
    public bool HasHoldFlag { get; set; } = false;
    public Guid? VoyageId { get; set; }
}

public class OutboundEligibilityCheckResultDto
{
    public string ContainerNo { get; set; } = string.Empty;
    public bool IsEligible { get; set; }
    public bool IsInYard { get; set; }
    public bool HasLoadList { get; set; }
    public bool HasValidVgm { get; set; }
    public bool IsCustomsCleared { get; set; }
    public bool HasNoHold { get; set; }
    public string? CurrentPosition { get; set; }
    public int StackTier { get; set; }
    public int DynamicLeadTimeMinutes { get; set; }
    public List<string> CheckLogs { get; set; } = new();
}

public class StackSlotDto
{
    public Guid SlotId { get; set; }
    public Guid BlockId { get; set; }
    public string BlockCode { get; set; } = string.Empty;
    public int Bay { get; set; }
    public int Row { get; set; }
    public int Tier { get; set; }
    public string? ContainerNo { get; set; }
    public string Status { get; set; } = "empty";
    public bool HasReeferPlug { get; set; }
    public bool IsReserved { get; set; }
    public string FormattedLocation => $"{BlockCode}-{Bay:D2}-{Row:D2}-{Tier}";
}

public class ContainerMovementDto
{
    public Guid Id { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public Guid FromSlotId { get; set; }
    public string FromSlotLocation { get; set; } = string.Empty;
    public Guid ToSlotId { get; set; }
    public string ToSlotLocation { get; set; } = string.Empty;
    public Guid? MovedBy { get; set; }
    public Guid? EquipmentId { get; set; }
    public DateTimeOffset MovedAt { get; set; }
    public Guid? WorkOrderId { get; set; }
    public string? Note { get; set; }
}

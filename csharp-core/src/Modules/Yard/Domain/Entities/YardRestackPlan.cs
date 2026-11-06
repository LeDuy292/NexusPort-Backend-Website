using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities;

/// <summary>
/// NXP-126: Kế hoạch di dời container chồng (Restacking / Reshuffling Plan)
/// Quản lý toàn bộ vòng đời của việc giải phóng container mục tiêu bị chặn tầng.
/// </summary>
public class YardRestackPlan : BaseEntity, IAggregateRoot
{
    public string PlanCode { get; set; } = string.Empty;
    public string TargetContainerNo { get; set; } = string.Empty;
    public string? TargetContainerType { get; set; } = "40FT HC";
    public string TargetLocation { get; set; } = string.Empty; // e.g. "C01-04-10-1"
    public string BlockCode { get; set; } = "C01";
    public string TargetDestination { get; set; } = "Xe Đầu Kéo Cổng C";
    public int TotalMoves { get; set; } = 0;
    public bool RestackBackToOriginal { get; set; } = false;
    
    // Trạng thái: Draft, Approved, In_Progress, Completed, Cancelled
    public string Status { get; set; } = "Draft";
    public int EstimatedMinutes { get; set; } = 5;
    public decimal EstimatedFee { get; set; } = 0;
    public bool IsBillable { get; set; } = false;
    public string? Notes { get; set; }
    public string? AssignedEquipmentCode { get; set; } = "RTG-01";
    public string? AssignedOperatorName { get; set; } = "Trần Văn Hùng";
    public DateTime? CompletedAt { get; set; }

    // Navigation property
    public List<YardRestackPlanStep> Steps { get; set; } = new();

    public YardRestackPlan() { }

    public YardRestackPlan(
        string planCode,
        string targetContainerNo,
        string targetLocation,
        string blockCode,
        string targetDestination,
        bool restackBackToOriginal = false,
        string? targetContainerType = "40FT HC",
        string? assignedEquipmentCode = "RTG-01",
        string? assignedOperatorName = "Trần Văn Hùng",
        string createdBy = "Yard Staff",
        string? notes = null)
    {
        PlanCode = planCode;
        TargetContainerNo = targetContainerNo;
        TargetLocation = targetLocation;
        BlockCode = blockCode;
        TargetDestination = targetDestination;
        RestackBackToOriginal = restackBackToOriginal;
        TargetContainerType = targetContainerType;
        AssignedEquipmentCode = assignedEquipmentCode;
        AssignedOperatorName = assignedOperatorName;
        CreatedBy = createdBy;
        Notes = notes;
        Status = "Draft";
    }
}

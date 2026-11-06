using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities;

/// <summary>
/// NXP-126: Chi tiết từng bước cẩu trong kế hoạch di dời container chồng
/// </summary>
public class YardRestackPlanStep : BaseEntity
{
    public Guid PlanId { get; set; }
    public int StepNumber { get; set; } // 1, 2, 3...
    public string ContainerNo { get; set; } = string.Empty;
    public string? ContainerType { get; set; } = "40FT HC";
    public bool IsTargetContainer { get; set; } = false; // true nếu là container mục tiêu
    
    // Loại bước: MoveToBuffer (Dời sang slot đệm), ReleaseTarget (Giải phóng cont mục tiêu), RestackBack (Trả về vị trí cũ)
    public string StepType { get; set; } = "MoveToBuffer";
    public string FromLocation { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    
    // Trạng thái: Pending, In_Progress, Completed, Cancelled
    public string Status { get; set; } = "Pending";
    public string? EquipmentCode { get; set; } = "RTG-01";
    public string? OperatorName { get; set; } = "Trần Văn Hùng";
    public int EstimatedDurationMinutes { get; set; } = 2;
    public decimal Fee { get; set; } = 0;
    public string? Reason { get; set; }
    
    // Liên kết tới lệnh tác nghiệp thực tế (Move Order / YardTask)
    public Guid? YardTaskId { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Navigation property
    public YardRestackPlan Plan { get; set; } = null!;

    public YardRestackPlanStep() { }

    public YardRestackPlanStep(
        Guid planId,
        int stepNumber,
        string containerNo,
        string fromLocation,
        string toLocation,
        bool isTargetContainer,
        string stepType = "MoveToBuffer",
        string? containerType = "40FT HC",
        string? equipmentCode = "RTG-01",
        string? operatorName = "Trần Văn Hùng",
        int estimatedDurationMinutes = 2,
        decimal fee = 0,
        string? reason = null)
    {
        PlanId = planId;
        StepNumber = stepNumber;
        ContainerNo = containerNo;
        FromLocation = fromLocation;
        ToLocation = toLocation;
        IsTargetContainer = isTargetContainer;
        StepType = stepType;
        ContainerType = containerType;
        EquipmentCode = equipmentCode;
        OperatorName = operatorName;
        EstimatedDurationMinutes = estimatedDurationMinutes;
        Fee = fee;
        Reason = reason;
        Status = "Pending";
    }
}

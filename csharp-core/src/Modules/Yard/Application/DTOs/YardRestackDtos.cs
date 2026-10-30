namespace NexusPort.Modules.Yard.Application.DTOs;

public class AnalyzeBlockedContainerRequestDto
{
    public string? ContainerNo { get; set; }
    public string? Location { get; set; } // e.g. "C01-04-10-1"
    public string? BlockCode { get; set; }
    public string TargetDestination { get; set; } = "Xe Đầu Kéo Cổng C";
    public bool RestackBackToOriginal { get; set; } = false;
}

public class BlockingContainerDto
{
    public int StepOrder { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public string ContainerType { get; set; } = "40FT HC";
    public string CurrentLocation { get; set; } = string.Empty;
    public int Tier { get; set; }
    public string Weight { get; set; } = "24.0 Tấn";
    public string ShippingLine { get; set; } = "MSC";
    public string RecommendedBufferSlot { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class AvailableBufferSlotDto
{
    public string Location { get; set; } = string.Empty;
    public string BlockCode { get; set; } = string.Empty;
    public int Bay { get; set; }
    public int Row { get; set; }
    public int Tier { get; set; }
    public string DistanceRating { get; set; } = "Gần (Cùng dãy)";
}

public class TargetContainerInfoDto
{
    public string ContainerNo { get; set; } = string.Empty;
    public string ContainerType { get; set; } = "40FT HC";
    public string CurrentLocation { get; set; } = string.Empty;
    public string BlockCode { get; set; } = "C01";
    public int Bay { get; set; } = 4;
    public int Row { get; set; } = 10;
    public int Tier { get; set; } = 1;
    public string Weight { get; set; } = "28.4 Tấn";
    public string ShippingLine { get; set; } = "MSC";
    public string DwellDays { get; set; } = "4 ngày";
    public string TargetDestination { get; set; } = "Xe Đầu Kéo Cổng C";
}

public class RestackAiMetricsDto
{
    public string TimeSaved { get; set; } = "75%";
    public string EnergySaved { get; set; } = "-68%";
    public string Confidence { get; set; } = "99.8%";
    public string BufferSlotNote { get; set; } = "Vị trí đệm an toàn, không gây tái xung đột trong 48h tới";
}

public class AnalyzeBlockedContainerResponseDto
{
    public TargetContainerInfoDto TargetContainer { get; set; } = new();
    public List<BlockingContainerDto> BlockingContainers { get; set; } = new();
    public List<AvailableBufferSlotDto> AvailableBufferSlots { get; set; } = new();
    public int TotalMoves { get; set; }
    public int EstimatedDurationMinutes { get; set; }
    public decimal EstimatedShiftingFee { get; set; }
    public bool CanExecute { get; set; } = true;
    public string ValidationMessage { get; set; } = string.Empty;
    public RestackAiMetricsDto AiMetrics { get; set; } = new();
}

public class CreateRestackPlanStepInputDto
{
    public int StepNumber { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public string? ContainerType { get; set; } = "40FT HC";
    public bool IsTargetContainer { get; set; }
    public string StepType { get; set; } = "MoveToBuffer";
    public string FromLocation { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? EquipmentCode { get; set; } = "RTG-01";
    public string? OperatorName { get; set; } = "Trần Văn Hùng";
}

public class CreateRestackPlanDto
{
    public string TargetContainerNo { get; set; } = string.Empty;
    public string TargetLocation { get; set; } = string.Empty;
    public string BlockCode { get; set; } = "C01";
    public string TargetDestination { get; set; } = "Xe Đầu Kéo Cổng C";
    public bool RestackBackToOriginal { get; set; } = false;
    public string? AssignedEquipmentCode { get; set; } = "RTG-01";
    public string? AssignedOperatorName { get; set; } = "Trần Văn Hùng";
    public bool IsBillable { get; set; } = false;
    public string? Notes { get; set; }
    public List<CreateRestackPlanStepInputDto>? Steps { get; set; }
}

public class RestackPlanStepDto
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public int StepNumber { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public string? ContainerType { get; set; }
    public bool IsTargetContainer { get; set; }
    public string StepType { get; set; } = "MoveToBuffer";
    public string FromLocation { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string? EquipmentCode { get; set; }
    public string? OperatorName { get; set; }
    public int EstimatedDurationMinutes { get; set; }
    public decimal Fee { get; set; }
    public string? Reason { get; set; }
    public Guid? YardTaskId { get; set; }
    public string? YardTaskCode { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class RestackPlanDto
{
    public Guid Id { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public string TargetContainerNo { get; set; } = string.Empty;
    public string? TargetContainerType { get; set; }
    public string TargetLocation { get; set; } = string.Empty;
    public string BlockCode { get; set; } = string.Empty;
    public string TargetDestination { get; set; } = string.Empty;
    public int TotalMoves { get; set; }
    public bool RestackBackToOriginal { get; set; }
    public string Status { get; set; } = "Draft";
    public int EstimatedMinutes { get; set; }
    public decimal EstimatedFee { get; set; }
    public bool IsBillable { get; set; }
    public string? Notes { get; set; }
    public string? AssignedEquipmentCode { get; set; }
    public string? AssignedOperatorName { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<RestackPlanStepDto> Steps { get; set; } = new();
}

public class DeployRestackPlanResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public RestackPlanDto Plan { get; set; } = new();
    public List<string> GeneratedTaskCodes { get; set; } = new();
}

public class AdvanceRestackStepDto
{
    public string? CompletedLocation { get; set; }
    public string? Notes { get; set; }
}

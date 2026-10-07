namespace NexusPort.Modules.Yard.Application.DTOs;

public class YardTaskDto
{
    public Guid Id { get; set; }
    public string TaskCode { get; set; } = string.Empty;
    public string OperationType { get; set; } = "Unload";
    public Guid? ContainerId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public string? ContainerType { get; set; }
    public string? CargoType { get; set; }
    public Guid? VehicleId { get; set; }
    public string? VehiclePlate { get; set; }
    public Guid? DriverId { get; set; }
    public string? DriverName { get; set; }
    public string? FromLocation { get; set; }
    public string? ToLocation { get; set; }
    public string BlockCode { get; set; } = string.Empty;
    public Guid? EquipmentId { get; set; }
    public string? EquipmentCode { get; set; }
    public string? EquipmentType { get; set; }
    public Guid? OperatorId { get; set; }
    public string? OperatorName { get; set; }
    public string Priority { get; set; } = "Normal";
    public string Status { get; set; } = "Assigned";
    public DateTime? DueTime { get; set; }
    public DateTime? AssignedAt { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ExpectedSealNo { get; set; }
    public string? ActualSealNo { get; set; }
    public string? Condition { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public string? ReceivedBy { get; set; }
    public string? CompletedLocation { get; set; }
    public decimal? InternalFee { get; set; }
    public string? ShiftingReason { get; set; }
    public int? DurationMinutes { get; set; }
    public string? SourceBlockCode { get; set; }
    public string? TargetBlockCode { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateRelocationTaskDto
{
    public string ContainerNo { get; set; } = string.Empty;
    public string? ContainerType { get; set; } = "40FT HC";
    public string? SourceBlockCode { get; set; }
    public string? FromLocation { get; set; } // e.g. "A01-03-02-1"
    public string TargetBlockCode { get; set; } = string.Empty;
    public string ToLocation { get; set; } = string.Empty; // e.g. "B02-04-01-2"
    public string ShiftingReason { get; set; } = "Tái cơ cấu xếp bãi"; // Tái cơ cấu xếp bãi, Chuẩn bị xuất cổng khẩn, Đảo tầng cẩu bãi, Theo yêu cầu khách hàng
    public string Priority { get; set; } = "Normal"; // Normal, High, Critical
    public Guid? EquipmentId { get; set; }
    public Guid? OperatorId { get; set; }
    public string? OperatorName { get; set; }
    public bool IsBillable { get; set; } = false;
    public decimal? InternalFee { get; set; }
    public string? Notes { get; set; }
}

public class ValidateSlotRequestDto
{
    public string TargetLocation { get; set; } = string.Empty; // e.g. "B02-04-01-2" or "B-02-08-3"
    public string? ContainerNo { get; set; }
    public string? ContainerType { get; set; }
}

public class ValidateSlotResponseDto
{
    public bool IsValid { get; set; }
    public string Message { get; set; } = string.Empty;
    public string TargetLocation { get; set; } = string.Empty;
    public string BlockCode { get; set; } = string.Empty;
    public int Bay { get; set; }
    public int Row { get; set; }
    public int Tier { get; set; }
    public string SlotStatus { get; set; } = "empty";
    public bool GravitySatisfied { get; set; } = true;
    public List<string> Warnings { get; set; } = new();
}

public class CalculateShiftingFeeRequestDto
{
    public string ContainerNo { get; set; } = string.Empty;
    public string? ContainerType { get; set; }
    public string? ShiftingReason { get; set; }
    public bool IsBillable { get; set; } = false;
}

public class CalculateShiftingFeeResponseDto
{
    public decimal Fee { get; set; }
    public string Currency { get; set; } = "VND";
    public string Description { get; set; } = string.Empty;
}

public class AssignEquipmentRequestDto
{
    public Guid EquipmentId { get; set; }
    public Guid OperatorId { get; set; }
    public string? OperatorName { get; set; }
    public string? Notes { get; set; }
}

public class StartLiftDto
{
    public string? Notes { get; set; }
}

public class CompleteLiftDto
{
    public string? CompletedLocation { get; set; }
    public string? Notes { get; set; }
}

public class YardReceivingInspectionDto
{
    public Guid? TaskId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public string? ExpectedSealNo { get; set; }
    public string ActualSealNo { get; set; } = string.Empty;
    public bool IsSealIntact { get; set; } = true;
    public string Condition { get; set; } = "Good"; // Good, Damaged, Seal_Broken
    public string? Notes { get; set; }
    public string? YardBlockCode { get; set; }
    public string? LocationCoordinate { get; set; }
    public string? InspectorName { get; set; }
}

public class CreateYardTaskDto
{
    public string TaskCode { get; set; } = string.Empty;
    public string OperationType { get; set; } = "Unload";
    public string ContainerNo { get; set; } = string.Empty;
    public string? ContainerType { get; set; } = "40FT HC";
    public string? CargoType { get; set; } = "Hàng Khô";
    public string BlockCode { get; set; } = "A01";
    public string? FromLocation { get; set; }
    public string? ToLocation { get; set; }
    public string? VehiclePlate { get; set; }
    public string? DriverName { get; set; }
    public string Priority { get; set; } = "Normal";
    public DateTime? DueTime { get; set; }
    public string? Notes { get; set; }
}

public class YardOperatorDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = "Yard Operator";
    public bool IsAvailable { get; set; } = true;
    public string? CurrentTaskCode { get; set; }
}


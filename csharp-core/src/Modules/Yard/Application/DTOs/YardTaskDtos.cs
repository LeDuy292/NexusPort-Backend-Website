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
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
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

using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities;

public class YardTask : BaseEntity, IAggregateRoot
{
    public string TaskCode { get; set; } = string.Empty;
    public string OperationType { get; set; } = "Unload"; // Unload, Load, Relocate, Inspection
    public Guid? ContainerId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public string? ContainerType { get; set; } = "40FT HC";
    public string? CargoType { get; set; } = "Hàng Khô";
    public Guid? VehicleId { get; set; }
    public string? VehiclePlate { get; set; }
    public Guid? DriverId { get; set; }
    public string? DriverName { get; set; }
    public string? FromLocation { get; set; }
    public string? ToLocation { get; set; }
    public string BlockCode { get; set; } = "A01";
    public Guid? EquipmentId { get; set; }
    public string? EquipmentCode { get; set; }
    public string? EquipmentType { get; set; }
    public Guid? OperatorId { get; set; }
    public string? OperatorName { get; set; }
    public string Priority { get; set; } = "Normal"; // Critical, High, Medium, Normal, Low
    public string Status { get; set; } = "Assigned"; // Assigned, Ready, In_Progress, Completed, Cancelled
    public DateTime? DueTime { get; set; }
    public DateTime? AssignedAt { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ExpectedSealNo { get; set; }
    public string? ActualSealNo { get; set; }
    public string? Condition { get; set; } // Good, Damaged, Seal_Broken
    public DateTime? ReceivedAt { get; set; }
    public string? ReceivedBy { get; set; }
    public string? CompletedLocation { get; set; }
    public string? Notes { get; set; }

    public YardTask() { }

    public YardTask(
        string taskCode,
        string containerNo,
        string blockCode,
        string operationType = "Unload",
        string? fromLocation = null,
        string? toLocation = null,
        string priority = "Normal",
        string status = "Assigned",
        string? containerType = "40FT HC",
        string? cargoType = "Hàng Khô",
        string? vehiclePlate = null,
        string? driverName = null,
        DateTime? dueTime = null,
        string? notes = null)
    {
        TaskCode = taskCode;
        ContainerNo = containerNo;
        BlockCode = blockCode;
        OperationType = operationType;
        FromLocation = fromLocation;
        ToLocation = toLocation;
        Priority = priority;
        Status = status;
        ContainerType = containerType;
        CargoType = cargoType;
        VehiclePlate = vehiclePlate;
        DriverName = driverName;
        DueTime = dueTime;
        Notes = notes;
    }
}

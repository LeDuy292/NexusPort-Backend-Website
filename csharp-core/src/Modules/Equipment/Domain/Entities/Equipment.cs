using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Equipment.Domain.Entities;

public class Equipment : BaseEntity, IAggregateRoot
{
    public string EquipmentCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EquipmentType { get; set; } = "RTG"; // RTG, QC, ReachStacker, Forklift
    public string Status { get; set; } = "available"; // available, working, maintenance, inactive
    public string BlockCode { get; set; } = "A01"; // A01, B02, C01, D01
    public Guid? OperatorId { get; set; }
    public string? OperatorName { get; set; }
    public string? Description { get; set; }

    public Equipment() { }

    public Equipment(string code, string name, string type, string blockCode, string status = "available", string? description = null)
    {
        EquipmentCode = code;
        Name = name;
        EquipmentType = type;
        BlockCode = blockCode;
        Status = status;
        Description = description;
    }
}


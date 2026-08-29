using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Equipment.Domain.Entities;

public class Equipment : BaseEntity, IAggregateRoot
{
    public string EquipmentCode { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public Equipment() { }

    public Equipment(string val, string? description = null)
    {
        EquipmentCode = val;
        Description = description;
    }
}

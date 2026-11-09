using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities;

public class YardBlock : BaseEntity, IAggregateRoot
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Zone { get; set; }
    
    public int MaxCapacity { get; set; }
    public int CurrentOccupancy { get; set; }
    public bool IsReeferArea { get; set; }
    public bool IsDangerousArea { get; set; }
    public bool IsOversizedArea { get; set; }
    public bool IsNearGate { get; set; }

    public ICollection<YardSlot> Slots { get; set; } = new List<YardSlot>();

    public YardBlock() { }

    public YardBlock(string code, string name)
    {
        Code = code;
        Name = name;
    }
}

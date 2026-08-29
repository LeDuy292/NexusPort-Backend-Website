using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities;

public class YardBlock : BaseEntity, IAggregateRoot
{
    public string BlockCode { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public YardBlock() { }

    public YardBlock(string val, string? description = null)
    {
        BlockCode = val;
        Description = description;
    }
}

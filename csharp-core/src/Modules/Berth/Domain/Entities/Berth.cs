using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Berth.Domain.Entities;

public class Berth : BaseEntity, IAggregateRoot
{
    public string BerthCode { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public Berth() { }

    public Berth(string val, string? description = null)
    {
        BerthCode = val;
        Description = description;
    }
}

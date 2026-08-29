using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Driver.Domain.Entities;

public class Driver : BaseEntity, IAggregateRoot
{
    public string FullName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public Driver() { }

    public Driver(string val, string? description = null)
    {
        FullName = val;
        Description = description;
    }
}

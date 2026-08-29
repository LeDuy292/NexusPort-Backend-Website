using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Identity.Domain.Entities;

public class User : BaseEntity, IAggregateRoot
{
    public string Username { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public User() { }

    public User(string val, string? description = null)
    {
        Username = val;
        Description = description;
    }
}

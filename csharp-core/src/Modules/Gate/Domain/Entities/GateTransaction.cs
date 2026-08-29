using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Gate.Domain.Entities;

public class GateTransaction : BaseEntity, IAggregateRoot
{
    public string TransactionCode { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public GateTransaction() { }

    public GateTransaction(string val, string? description = null)
    {
        TransactionCode = val;
        Description = description;
    }
}

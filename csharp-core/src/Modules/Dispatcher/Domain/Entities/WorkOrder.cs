using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Dispatcher.Domain.Entities;

public class WorkOrder : BaseEntity, IAggregateRoot
{
    public string OrderNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public WorkOrder() { }

    public WorkOrder(string val, string? description = null)
    {
        OrderNumber = val;
        Description = description;
    }
}

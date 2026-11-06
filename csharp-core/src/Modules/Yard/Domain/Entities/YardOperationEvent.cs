using NexusPort.Shared.Kernel;
using NexusPort.Modules.Yard.Domain.Events;

namespace NexusPort.Modules.Yard.Domain.Entities;

/// <summary>Durable outbox record for a completed yard operation.</summary>
public class YardOperationEvent : BaseEntity
{
    public Guid OperationId { get; set; }
    public Guid ContainerId { get; set; }
    public Guid DriverId { get; set; }
    public string OperationStatus { get; set; } = "Completed";
    public string EventType { get; set; } = YardOperationCompletedEvent.EventName;
    public string DeliveryStatus { get; set; } = "Pending";
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public string? DeliveryError { get; set; }

    public void MarkPublished()
    {
        DeliveryStatus = "Published";
        PublishedAt = DateTime.UtcNow;
        DeliveryError = null;
    }

    public void MarkFailed(string error)
    {
        DeliveryStatus = "Failed";
        DeliveryError = error.Length > 1000 ? error[..1000] : error;
    }
}

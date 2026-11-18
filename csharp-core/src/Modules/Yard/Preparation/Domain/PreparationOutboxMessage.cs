using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Preparation.Domain;

public class PreparationOutboxMessage : BaseEntity
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public int RetryCount { get; set; } = 0;
    public string? Error { get; set; }

    public PreparationOutboxMessage() { }

    public PreparationOutboxMessage(string eventType, string payload)
    {
        Id = Guid.NewGuid();
        EventId = Guid.NewGuid().ToString("N");
        EventType = eventType;
        Payload = payload;
        Version = 1;
        OccurredAt = DateTimeOffset.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }
}

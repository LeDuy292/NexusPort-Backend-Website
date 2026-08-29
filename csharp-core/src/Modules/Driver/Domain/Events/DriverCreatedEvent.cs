using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Driver.Domain.Events;

public record DriverCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

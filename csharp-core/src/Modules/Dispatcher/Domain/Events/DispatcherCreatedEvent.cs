using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Dispatcher.Domain.Events;

public record DispatcherCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

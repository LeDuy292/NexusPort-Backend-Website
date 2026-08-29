using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Container.Domain.Events;

public record ContainerCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

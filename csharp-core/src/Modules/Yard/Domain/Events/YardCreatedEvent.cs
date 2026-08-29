using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Events;

public record YardCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

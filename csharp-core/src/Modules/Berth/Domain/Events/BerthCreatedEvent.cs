using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Berth.Domain.Events;

public record BerthCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

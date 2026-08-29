using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Gate.Domain.Events;

public record GateCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Vessel.Domain.Events;

public record VesselCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Vehicle.Domain.Events;

public record VehicleCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

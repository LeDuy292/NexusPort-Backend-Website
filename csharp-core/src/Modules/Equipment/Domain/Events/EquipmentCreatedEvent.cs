using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Equipment.Domain.Events;

public record EquipmentCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

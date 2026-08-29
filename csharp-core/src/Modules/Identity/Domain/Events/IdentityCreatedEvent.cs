using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Identity.Domain.Events;

public record IdentityCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

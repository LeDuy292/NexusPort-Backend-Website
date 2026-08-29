using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Booking.Domain.Events;

public record BookingCreatedEvent(Guid EntityId, string Name) : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}

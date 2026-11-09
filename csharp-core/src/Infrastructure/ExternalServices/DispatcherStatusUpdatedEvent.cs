namespace NexusPort.Infrastructure.ExternalServices;

/// <summary>Shared integration contract consumed by the Dispatcher realtime dashboard.</summary>
public sealed record DispatcherStatusUpdatedEvent(
    Guid EventId,
    string Domain,
    string EntityId,
    string Status,
    DateTime OccurredAt,
    Guid? BookingId = null,
    Guid? ContainerId = null,
    Guid? VehicleId = null,
    Guid? DriverId = null,
    string? Label = null);

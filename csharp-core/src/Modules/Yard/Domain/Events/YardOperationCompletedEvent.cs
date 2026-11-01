namespace NexusPort.Modules.Yard.Domain.Events;

/// <summary>Message contract for the Node.js realtime gateway.</summary>
public sealed record YardOperationCompletedEvent(
    Guid EventId,
    Guid OperationId,
    Guid ContainerId,
    Guid DriverId,
    string OperationStatus,
    DateTime CompletedAt)
{
    public const string EventName = "yard.operation.completed";
}

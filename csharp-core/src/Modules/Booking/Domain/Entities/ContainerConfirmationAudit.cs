using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Booking.Domain.Entities;

public class ContainerConfirmationAudit : BaseEntity
{
    public Guid BookingId { get; set; }
    public Guid ContainerId { get; set; }
    public Guid DriverId { get; set; }
    public string Condition { get; set; } = "OK";
    public string? Notes { get; set; }
    public string ContainerStatusAfter { get; set; } = "ReadyForGateOut";
    public DateTime ConfirmedAt { get; set; } = DateTime.UtcNow;
}

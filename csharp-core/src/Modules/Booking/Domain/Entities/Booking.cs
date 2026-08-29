using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Booking.Domain.Entities;

public class Booking : BaseEntity, IAggregateRoot
{
    public string BookingNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? Description { get; set; }

    public Booking() { }

    public Booking(string val, string? description = null)
    {
        BookingNumber = val;
        Description = description;
    }
}

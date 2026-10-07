namespace NexusPort.Modules.Booking.Domain.Entities;

public class BookingContainer
{
    public Guid BookingId { get; set; }
    public Booking? Booking { get; set; }

    public Guid ContainerId { get; set; }
    public string? Note { get; set; }

    public BookingContainer() { }

    public BookingContainer(Guid bookingId, Guid containerId, string? note = null)
    {
        BookingId = bookingId;
        ContainerId = containerId;
        Note = note;
    }
}

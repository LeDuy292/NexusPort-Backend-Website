namespace NexusPort.Modules.Booking.Application.DTOs;

public class BookingDto
{
    public Guid Id { get; set; }
    public string BookingNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateBookingDto
{
    public string BookingNumber { get; set; } = string.Empty;
    public string? Description { get; set; }
}

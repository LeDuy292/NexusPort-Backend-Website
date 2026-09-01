using NexusPort.Modules.Booking.Domain.Enums;

namespace NexusPort.Modules.Booking.Application.DTOs;

public class BookingDto
{
    public Guid Id { get; set; }
    public Guid CarrierId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public BookingType BookingType { get; set; }
    public BookingStatus Status { get; set; }
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectedReason { get; set; }
    public DateTime? CanceledAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Guid> ContainerIds { get; set; } = new();
}

public class CreateBookingDto
{
    public Guid CarrierId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public BookingType BookingType { get; set; } = BookingType.Pickup;
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public List<Guid> ContainerIds { get; set; } = new();
}

public class UpdateBookingDto
{
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public List<Guid> ContainerIds { get; set; } = new();
}

public class CancelBookingDto
{
    public string? Reason { get; set; }
}

public class BookingFilterParams
{
    public Guid? CarrierId { get; set; }
    public string? Search { get; set; }
    public BookingStatus? Status { get; set; }
    public BookingType? BookingType { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

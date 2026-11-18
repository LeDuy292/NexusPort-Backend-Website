using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Preparation.Domain;

public class SlotBooking : BaseEntity
{
    public string BookingCode { get; set; } = string.Empty;
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public string Status { get; set; } = "confirmed";

    public SlotBooking() { }

    public SlotBooking(Guid id, string bookingCode, DateTime appointmentStart, DateTime appointmentEnd, string status = "confirmed")
    {
        Id = id;
        BookingCode = bookingCode;
        AppointmentStart = appointmentStart;
        AppointmentEnd = appointmentEnd;
        Status = status;
    }
}

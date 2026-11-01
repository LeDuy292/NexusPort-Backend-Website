using System.Text.Json.Serialization;

namespace NexusPort.Modules.Booking.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookingStatus
{
    Pending,
    Ready,
    Approved,
    Rejected,
    Canceled,
    CheckedIn,
    Completed,
    Expired
}

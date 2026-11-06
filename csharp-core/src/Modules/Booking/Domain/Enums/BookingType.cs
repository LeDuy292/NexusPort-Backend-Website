using System.Text.Json.Serialization;

namespace NexusPort.Modules.Booking.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookingType
{
    Pickup,
    Dropoff
}

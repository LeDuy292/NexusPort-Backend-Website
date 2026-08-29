using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Booking.Domain.ValueObjects;

public class BookingCode : ValueObject
{
    public string Value { get; }

    public BookingCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Driver.Domain.ValueObjects;

public class DriverCode : ValueObject
{
    public string Value { get; }

    public DriverCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

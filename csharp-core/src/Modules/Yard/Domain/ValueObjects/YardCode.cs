using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.ValueObjects;

public class YardCode : ValueObject
{
    public string Value { get; }

    public YardCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

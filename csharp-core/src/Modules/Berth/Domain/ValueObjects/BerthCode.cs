using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Berth.Domain.ValueObjects;

public class BerthCode : ValueObject
{
    public string Value { get; }

    public BerthCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

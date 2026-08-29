using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Gate.Domain.ValueObjects;

public class GateCode : ValueObject
{
    public string Value { get; }

    public GateCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

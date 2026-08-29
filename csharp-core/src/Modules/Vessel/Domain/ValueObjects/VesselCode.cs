using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Vessel.Domain.ValueObjects;

public class VesselCode : ValueObject
{
    public string Value { get; }

    public VesselCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

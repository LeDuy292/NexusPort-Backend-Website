using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Vehicle.Domain.ValueObjects;

public class VehicleCode : ValueObject
{
    public string Value { get; }

    public VehicleCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Equipment.Domain.ValueObjects;

public class EquipmentCode : ValueObject
{
    public string Value { get; }

    public EquipmentCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

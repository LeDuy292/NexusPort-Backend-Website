using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Container.Domain.ValueObjects;

public class ContainerCode : ValueObject
{
    public string Value { get; }

    public ContainerCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

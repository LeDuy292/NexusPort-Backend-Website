using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Identity.Domain.ValueObjects;

public class IdentityCode : ValueObject
{
    public string Value { get; }

    public IdentityCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Dispatcher.Domain.ValueObjects;

public class DispatcherCode : ValueObject
{
    public string Value { get; }

    public DispatcherCode(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}

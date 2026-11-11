namespace NexusPort.Modules.ServiceCatalog.Domain;

public sealed record ServicePaymentCreateInput
{
    public Guid RequestId { get; init; }
    public int ExpectedVersion { get; init; }
}

public sealed record ServicePaymentSyncInput
{
    public Guid RequestId { get; init; }
    public int ExpectedVersion { get; init; }
}

public sealed record ServicePaymentCancelInput
{
    public Guid RequestId { get; init; }
    public int ExpectedVersion { get; init; }
    public string Reason { get; init; } = "";
}

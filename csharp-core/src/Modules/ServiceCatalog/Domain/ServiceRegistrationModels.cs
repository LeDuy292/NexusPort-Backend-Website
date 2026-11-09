namespace NexusPort.Modules.ServiceCatalog.Domain;

public sealed record ServiceValidationInput
{
    public int ExpectedVersion { get; init; }
}

public sealed record ServiceConfirmationInput
{
    public int ExpectedVersion { get; init; }
    public string QuoteToken { get; init; } = "";
}

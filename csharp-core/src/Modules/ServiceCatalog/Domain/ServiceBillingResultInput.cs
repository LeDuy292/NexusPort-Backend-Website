namespace NexusPort.Modules.ServiceCatalog.Domain;

// Integration response supplied by the Dispatcher module, never by a customer.
public sealed record ServiceBillingResultInput
{
    public Guid RequestId { get; init; }
    public int ExpectedVersion { get; init; }
    public string Status { get; init; } = "APPROVED";
    public string Reference { get; init; } = "";
    public decimal? Total { get; init; }
    public string Currency { get; init; } = "VND";
    public string Reason { get; init; } = "";
    public bool SourceDocumentsChecked { get; init; }
    public string SourceEvidenceReference { get; init; } = "";
}

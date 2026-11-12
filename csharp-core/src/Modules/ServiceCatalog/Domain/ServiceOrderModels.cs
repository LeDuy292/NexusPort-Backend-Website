namespace NexusPort.Modules.ServiceCatalog.Domain;

public sealed record ServiceOrderActionInput
{
    public int ExpectedVersion { get; init; }
    public Guid RequestId { get; init; }
    public string Action { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool SourceDocumentsChecked { get; init; }
    public string SourceEvidenceReference { get; init; } = "";
}

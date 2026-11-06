namespace NexusPort.Modules.Booking.Application.DTOs;

public sealed class ExportEquipmentRequest
{
    public string TypeCode { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class ExportPreparationDocument : CreateBookingDocumentDto
{
    public Guid? DocumentId { get; set; }
    public List<string> ContainerNumbers { get; set; } = new();
}

public sealed class ExportPreparedContainer
{
    public Guid ContainerId { get; set; }
    public Guid DeclarationId { get; set; }
    public string? ContainerNumber { get; set; }
    public string? ContainerTypeCode { get; set; }
    public string? CargoType { get; set; }
    public decimal? GrossWeightKg { get; set; }
    public decimal? RequiredPowerKw { get; set; }
    public string SealNumber { get; set; } = "";
    public decimal? VgmKg { get; set; }
    public decimal? TargetTemperature { get; set; }
    public string? UnNumber { get; set; }
    public string? DgClass { get; set; }
    public bool IsReeferPowerRequired { get; set; }
    public List<ExportPreparationDocument> Documents { get; set; } = new();
}

// Incomplete information is allowed in a private draft. Submission has separate validation.
public sealed class ExportPreparationContent
{
    public int SchemaVersion { get; set; } = 1;
    public Guid? BookingId { get; set; }
    public Guid? ShippingLineId { get; set; }
    public Guid? VoyageId { get; set; }
    public string Mode { get; set; } = "containers";
    public string? Description { get; set; }
    public List<ExportEquipmentRequest> EquipmentRequests { get; set; } = new();
    public List<ExportPreparedContainer> Containers { get; set; } = new();
    public List<ExportPreparationDocument> SharedDocuments { get; set; } = new();
    public bool SharedDocumentsConfirmed { get; set; }
}

public sealed class ExportDraftSaveRequest
{
    public Guid RequestId { get; set; }
    public int ExpectedVersion { get; set; }
    public ExportPreparationContent Content { get; set; } = new();
}

public sealed class ExportPreparationRequest
{
    public Guid RequestId { get; set; }
    public Guid? BookingId { get; set; }
    public Guid? DraftId { get; set; }
    public int ExpectedDraftVersion { get; set; }
    public int ExpectedVersion { get; set; }
    public ExportPreparationContent Content { get; set; } = new();
}

public sealed class ExportContainerReviewRequest : ExportVersionRequest
{
    public bool Approve { get; set; }
}

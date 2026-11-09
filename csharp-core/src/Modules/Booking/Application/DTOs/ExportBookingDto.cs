namespace NexusPort.Modules.Booking.Application.DTOs;

public class ExportVersionRequest
{
    public int ExpectedVersion { get; set; }
    public string? Reason { get; set; }
    public int DraftVersion { get; set; }
    public bool IsPaid { get; set; }
}
public sealed class CreateExportBookingRequest
{
    public Guid RequestId { get; set; }
    public Guid ShippingLineId { get; set; }
    public Guid VoyageId { get; set; }
    public string ContainerTypeCode { get; set; } = "";
    public int Quantity { get; set; }
    public string CommodityDescription { get; set; } = "";
}
public sealed class CreateExportVoyageRequest
{
    public Guid ShippingLineId { get; set; }
    public string VesselName { get; set; } = "";
    public string VoyageNumber { get; set; } = "";
    public string PortOfLoading { get; set; } = "";
    public string PortOfDischarge { get; set; } = "";
    public DateTime Etd { get; set; }
    public DateTime Eta { get; set; }
    public DateTime ReceiveFrom { get; set; }
    public DateTime CyCutoff { get; set; }
    public DateTime VgmCutoff { get; set; }
    public DateTime SiCutoff { get; set; }
}
public class ExportContainerInput
{
    public Guid ContainerId { get; set; }
    public Guid DeclarationId { get; set; }
    public string SealNumber { get; set; } = "";
    public decimal VgmKg { get; set; }
    public decimal? TargetTemperature { get; set; }
    public string? UnNumber { get; set; }
    public string? DgClass { get; set; }
    public string? CustomsDocumentUrl { get; set; }
}
public sealed class DeclareExportContainersRequest : ExportVersionRequest
{
    public List<ExportContainerInput> Containers { get; set; } = new();
}
public sealed class ExportSiRequest : ExportVersionRequest
{
    public string Shipper { get; set; } = "";
    public string Consignee { get; set; } = "";
    public string? NotifyParty { get; set; }
    public string CargoDescription { get; set; } = "";
    public int PackagesCount { get; set; }
    public decimal GrossWeightKg { get; set; }
    public string FreightTerms { get; set; } = "Prepaid";
    public string BlType { get; set; } = "Original";
    public string DestinationPort { get; set; } = "";
}
public sealed class ExportPortEventRequest
{
    public Guid EventId { get; set; }
    public Guid ExportContainerId { get; set; }
    public string EventType { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public string? Reason { get; set; }
}

// One customer submission contains cargo, documents and proposed truck appointments.
// Appointments become port requests only after the shipping line approves the submission.
public sealed class ExportSubmissionRequest
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ExportSubmissionPickup? Pickup { get; set; }
    public bool OptimizeSchedule { get; set; }
    public Guid? BookingId { get; set; }
    public int ExpectedVersion { get; set; }
    public Guid RequestId { get; set; }
    public Guid ShippingLineId { get; set; }
    public Guid VoyageId { get; set; }
    public string? Description { get; set; }
    public bool SharedDocumentsConfirmed { get; set; }
    public List<BookingBatchDocumentDto> SharedDocuments { get; set; } = new();
    public List<ExportSubmissionTrip> Trips { get; set; } = new();
}

public sealed class ExportSubmissionTrip : ExportContainerInput
{
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public Guid? TruckId { get; set; }
    public Guid? DriverId { get; set; }
    public string? TrailerNumber { get; set; }
    public bool IsReeferPowerRequired { get; set; }
    public List<CreateBookingDocumentDto> Documents { get; set; } = new();
}

public sealed class ExportSubmissionPickup
{
    public Guid ContainerId { get; set; }
    public string YardCode { get; set; } = "";
    public decimal? TargetTemperature { get; set; }
    public string? UnNumber { get; set; }
    public string? DgClass { get; set; }
    public List<CreateBookingDocumentDto> Documents { get; set; } = new();
}

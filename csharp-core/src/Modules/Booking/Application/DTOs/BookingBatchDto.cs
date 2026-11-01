using NexusPort.Modules.Booking.Domain.Enums;

namespace NexusPort.Modules.Booking.Application.DTOs;

public sealed class BookingBatchRequest
{
    public Guid RequestId { get; set; }
    public Guid CarrierId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? CreatedBy { get; set; }
    public BookingType BookingType { get; set; } = BookingType.Dropoff;
    public string? Description { get; set; }
    public bool SharedDocumentsConfirmed { get; set; }
    public bool AllowUnassignedTrips { get; set; }
    public List<BookingBatchDocumentDto> SharedDocuments { get; set; } = new();
    public List<BookingBatchTripRequest> Trips { get; set; } = new();
}

public sealed class BookingBatchTripRequest
{
    public BookingContainerSelectionDto Selection { get; set; } = new();
    public CreateBookingOperationDto Operation { get; set; } = new();
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public Guid? TruckId { get; set; }
    public Guid? DriverId { get; set; }
    public string? TrailerNumber { get; set; }
    public bool IsReeferPowerRequired { get; set; }
    public decimal? TempTolerance { get; set; } = 2;
}

public sealed class BookingBatchDocumentDto : CreateBookingDocumentDto
{
    // Declared coverage for reviewer comparison with the uploaded original; never proof of verification.
    public List<string> ContainerNumbers { get; set; } = new();
}

public sealed class BookingBatchPlan
{
    public string CargoType { get; set; } = "";
    public List<string> ValidationMessages { get; set; } = new();
    public List<BookingBatchTripPlan> Trips { get; set; } = new();
}
public sealed class BookingBatchTripPlan
{
    public Guid ContainerId { get; set; }
    public bool CargoAvailable { get; set; }
    public BookingPlan Plan { get; set; } = new();
    public Guid? TruckId { get; set; }
    public Guid? DriverId { get; set; }
    public string? TrailerNumber { get; set; }
    public List<BookingBatchTrailerSuggestion> TrailerSuggestions { get; set; } = new();
}
public sealed class BookingBatchTrailerSuggestion
{
    public Guid TruckId { get; set; }
    public string TrailerNumber { get; set; } = "";
}
public sealed class BookingBatchDto
{
    public Guid Id { get; set; }
    public string BatchCode { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int TotalTrips { get; set; }
    public int CompletedTrips { get; set; }
    public int CanceledTrips { get; set; }
    public int UnassignedTrips { get; set; }
    public BookingType BookingType { get; set; }
    public string CargoType { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime DocumentsConfirmedAt { get; set; }
    public List<BookingBatchDocumentDto> SharedDocuments { get; set; } = new();
    public List<BookingBatchTripDto> Trips { get; set; } = new();
}
public sealed class BookingBatchTripDto
{
    public int SequenceNumber { get; set; }
    public BookingDto Booking { get; set; } = new();
    public bool GateInCompleted { get; set; }
    public bool GateOutCompleted { get; set; }
}

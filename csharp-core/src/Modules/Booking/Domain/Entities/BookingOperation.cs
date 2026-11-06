using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Booking.Domain.Entities;

public class BookingOperation : BaseEntity
{
    public Guid BookingId { get; set; }
    public Booking? Booking { get; set; }

    public string OperationType { get; set; } = "Export"; // Export, Import
    public int SequenceNumber { get; set; } = 1;
    public string ExpectedGateDirection { get; set; } = "GateIn";
    public string Status { get; set; } = "PendingVerification";

    public Guid? ContainerId { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string ContainerIsoSize { get; set; } = "20GP";
    public string CargoStatus { get; set; } = "Loaded";
    public string CargoType { get; set; } = "General";

    public string? CarrierBookingNumber { get; set; }
    public string? BillOfLadingNumber { get; set; }
    public string? DeliveryOrderNumber { get; set; }
    public string? CustomsDeclarationNumber { get; set; }
    public string? SealNumber { get; set; }
    public decimal? GrossWeightKg { get; set; }

    // Special handling properties
    public decimal? TemperatureSetpoint { get; set; }
    public string? TemperatureUnit { get; set; } = "C";
    public string? UnNumber { get; set; }
    public string? DgClass { get; set; }
    public string? SpecialHandlingRequirements { get; set; }

    // Verification by gate staff / dispatcher
    public string VerificationStatus { get; set; } = "Pending"; // Pending, Verified, Rejected, Exempt
    public Guid? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerificationNotes { get; set; }

    // EIR & Actual gate event confirmation
    public string? EirNumber { get; set; }
    public DateTime? EirIssuedAt { get; set; }
    public string? ContainerCondition { get; set; } = "Good";

    public ICollection<BookingDocument> Documents { get; set; } = new List<BookingDocument>();

    public BookingOperation() { }

    public BookingOperation(
        Guid bookingId,
        string operationType,
        string containerNumber,
        string expectedGateDirection,
        string cargoStatus = "Loaded",
        int sequenceNumber = 1)
    {
        BookingId = bookingId;
        OperationType = operationType;
        ContainerNumber = containerNumber;
        ExpectedGateDirection = expectedGateDirection;
        CargoStatus = cargoStatus;
        SequenceNumber = sequenceNumber;
        Status = "PendingVerification";
        VerificationStatus = "Pending";
    }

    public void Approve()
    {
        Status = "Approved";
        VerificationStatus = "Verified";
    }

    public void StartProgress()
    {
        Status = "InProgress";
    }

    public void Complete(string? eirNo = null)
    {
        Status = "Completed";
        if (!string.IsNullOrWhiteSpace(eirNo))
        {
            EirNumber = eirNo;
            EirIssuedAt = DateTime.UtcNow;
        }
    }

    public void PutOnHold(string reason)
    {
        Status = "OnHold";
        VerificationNotes = reason;
    }

    public void Reject(string reason)
    {
        Status = "Rejected";
        VerificationStatus = "Rejected";
        VerificationNotes = reason;
    }

    public void Cancel()
    {
        Status = "Cancelled";
    }
}

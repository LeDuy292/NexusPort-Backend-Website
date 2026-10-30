using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Booking.Domain.Entities;

public class BookingDocument : BaseEntity
{
    public Guid BookingId { get; set; }
    public Booking? Booking { get; set; }

    public Guid? BookingOperationId { get; set; }
    public BookingOperation? BookingOperation { get; set; }

    public string DocumentType { get; set; } = "Other";
    public string? DocumentNumber { get; set; }
    public string? FileUrl { get; set; }
    public string? FileName { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? Issuer { get; set; }

    public string VerificationStatus { get; set; } = "Uploaded"; // Missing, Uploaded, UnderVerification, Verified, Rejected, Expired
    public Guid? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime? ExpiryDate { get; set; }

    public BookingDocument() { }

    public BookingDocument(
        Guid bookingId,
        string documentType,
        string? documentNumber = null,
        string? fileUrl = null,
        string? fileName = null,
        Guid? operationId = null)
    {
        BookingId = bookingId;
        DocumentType = documentType;
        DocumentNumber = documentNumber;
        FileUrl = fileUrl;
        FileName = fileName;
        BookingOperationId = operationId;
        VerificationStatus = string.IsNullOrWhiteSpace(fileUrl) ? "Missing" : "Uploaded";
    }

    public void Verify(Guid verifiedBy)
    {
        VerificationStatus = "Verified";
        VerifiedBy = verifiedBy;
        VerifiedAt = DateTime.UtcNow;
        RejectionReason = null;
    }

    public void Reject(Guid rejectedBy, string reason)
    {
        VerificationStatus = "Rejected";
        VerifiedBy = rejectedBy;
        VerifiedAt = DateTime.UtcNow;
        RejectionReason = reason;
    }
}

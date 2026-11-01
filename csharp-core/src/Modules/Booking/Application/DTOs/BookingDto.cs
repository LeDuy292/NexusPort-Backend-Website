using NexusPort.Modules.Booking.Domain.Enums;

namespace NexusPort.Modules.Booking.Application.DTOs;

public class BookingDto
{
    public BookingVehicleDetailsDto? VehicleDetails { get; set; }
    public BookingDriverDetailsDto? DriverDetails { get; set; }
    public string? TrailerNumber { get; set; }
    public Guid Id { get; set; }
    public Guid CarrierId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string BookingNumber { get => BookingCode; set => BookingCode = value; }
    public BookingType BookingType { get; set; }
    public BookingStatus Status { get; set; }
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectedReason { get; set; }
    public DateTime? CanceledAt { get; set; }
    public string? Description { get; set; }

    // Associated Vehicle & Driver details
    public string? VehiclePlate { get; set; }
    public Guid? VehicleId { get; set; }
    public string? DriverName { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? GateType { get; set; }

    public DateTime CreatedAt { get; set; }
    public List<Guid> ContainerIds { get; set; } = new();
    public List<BookingContainerSelectionDto> ContainerSelections { get; set; } = new();
    public List<string> ContainerNumbers { get; set; } = new();

    // Multi-operation and combined trip properties
    public bool IsCombined { get; set; }
    public string TripStatus { get; set; } = "Scheduled";
    public Guid? CustomerId { get; set; }
    public List<BookingOperationDto> Operations { get; set; } = new();
    public List<BookingDocumentDto> Documents { get; set; } = new();

    // Payment & TOS Tariff details
    public string PaymentStatus { get; set; } = "Unpaid";
    public decimal TotalAmount { get; set; } = 583200m;
    public string? InvoiceNo { get; set; }
    public string? TransactionRef { get; set; }
    public DateTime? PaidAt { get; set; }

    // Cold Storage Reefer Power Supply details
    public bool IsReeferPowerRequired { get; set; }
    public decimal? TargetTemperature { get; set; }
    public string? TemperatureUnit { get; set; } = "C";
    public decimal? TempTolerance { get; set; } = 2.0m;
    public Guid? ReeferPlugId { get; set; }
    public string? ReeferPlugCode { get; set; }
    public string? ReeferPowerStatus { get; set; }
    public decimal? RequiredPowerKw { get; set; } = 15.0m;
}

public class BookingOperationDto
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string OperationType { get; set; } = "Export";
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

    public decimal? TemperatureSetpoint { get; set; }
    public string? TemperatureUnit { get; set; } = "C";
    public string? UnNumber { get; set; }
    public string? DgClass { get; set; }
    public string? SpecialHandlingRequirements { get; set; }

    public string VerificationStatus { get; set; } = "Pending";
    public Guid? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerificationNotes { get; set; }

    public string? EirNumber { get; set; }
    public DateTime? EirIssuedAt { get; set; }
    public string? ContainerCondition { get; set; } = "Good";

    public List<BookingDocumentDto> Documents { get; set; } = new();
}

public class CreateBookingOperationDto
{
    public string OperationType { get; set; } = "Export";
    public int SequenceNumber { get; set; } = 1;
    public string ExpectedGateDirection { get; set; } = "GateIn";
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

    public decimal? TemperatureSetpoint { get; set; }
    public string? TemperatureUnit { get; set; } = "C";
    public string? UnNumber { get; set; }
    public string? DgClass { get; set; }
    public string? SpecialHandlingRequirements { get; set; }

    public List<CreateBookingDocumentDto> Documents { get; set; } = new();
}

public class BookingDocumentDto
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public Guid? BookingOperationId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string? DocumentNumber { get; set; }
    public string? FileUrl { get; set; }
    public string? FileName { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? Issuer { get; set; }
    public string VerificationStatus { get; set; } = "Uploaded";
    public Guid? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateBookingDocumentDto
{
    public Guid? BookingOperationId { get; set; }
    public string DocumentType { get; set; } = "Other";
    public string? DocumentNumber { get; set; }
    public string? FileUrl { get; set; }
    public string? FileName { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? Issuer { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

public class VerifyBookingDocumentDto
{
    public bool IsApproved { get; set; }
    public string? Reason { get; set; }
}

public class UpdateBookingOperationStatusDto
{
    public string Status { get; set; } = "Approved"; // Approved, OnHold, InProgress, Completed, Rejected
    public string? Notes { get; set; }
    public string? EirNumber { get; set; }
    public string? ContainerCondition { get; set; }
}

public class BookingContainerSelectionDto
{
    public Guid ContainerId { get; set; }
    public Guid DeclarationId { get; set; }
    public Guid? ContainerVisitId { get; set; }
    public string? YardCode { get; set; }
    public string? ContainerNumber { get; set; }
    public string? ContainerTypeCode { get; set; }
    public string? CargoTypeName { get; set; }
}

public class CreateBookingDto
{
    public string? TrailerNumber { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? ExcludeBookingId { get; set; }
    public Guid CarrierId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string BookingNumber { get => BookingCode; set => BookingCode = value; }
    public BookingType BookingType { get; set; } = BookingType.Pickup;
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public List<Guid> ContainerIds { get; set; } = new();
    [System.ComponentModel.DataAnnotations.Required]
    public List<BookingContainerSelectionDto> ContainerSelections { get; set; } = new();

    public string? Status { get; set; } = "Active";
    public string? Description { get; set; }
    public string? VehiclePlate { get; set; }
    public Guid? VehicleId { get; set; }
    public string? DriverName { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? GateType { get; set; } = "GateIn";

    // Multi-operation and combined trip additions
    public bool IsCombined { get; set; }
    public Guid? CustomerId { get; set; }
    [System.ComponentModel.DataAnnotations.Required]
    public List<CreateBookingOperationDto> Operations { get; set; } = new();
    [System.ComponentModel.DataAnnotations.Required]
    public List<CreateBookingDocumentDto> Documents { get; set; } = new();

    // Cold Storage Reefer Power Supply configuration
    public bool IsReeferPowerRequired { get; set; }
    public decimal? TargetTemperature { get; set; }
    public string? TemperatureUnit { get; set; } = "C";
    public decimal? TempTolerance { get; set; } = 2.0m;
    public Guid? ReeferPlugId { get; set; }
    public string? ReeferPlugCode { get; set; }
    public decimal? RequiredPowerKw { get; set; } = 15.0m;
    public string? SpecialColdInstructions { get; set; }
}

public class UpdateBookingDto
{
    public string? TrailerNumber { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public List<Guid> ContainerIds { get; set; } = new();
    public List<BookingContainerSelectionDto> ContainerSelections { get; set; } = new();
}

public class CancelBookingDto
{
    public string? Reason { get; set; }
}

public class BookingFilterParams
{
    public Guid? CarrierId { get; set; }
    public string? Search { get; set; }
    public BookingStatus? Status { get; set; }
    public BookingType? BookingType { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

public class DriverContainerOperationDto
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public BookingType BookingType { get; set; }
    public BookingStatus BookingStatus { get; set; }
    public Guid ContainerId { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string ContainerStatus { get; set; } = string.Empty;
    public string OperationStatus { get; set; } = string.Empty;
}

public class ContainerConfirmationDto
{
    public Guid BookingId { get; set; }
    public Guid ContainerId { get; set; }
    public string Condition { get; set; } = "OK";
    public string? Notes { get; set; }
}

public class ContainerConfirmationResultDto
{
    public Guid ConfirmationId { get; set; }
    public Guid BookingId { get; set; }
    public Guid ContainerId { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string ContainerStatus { get; set; } = string.Empty;
    public BookingStatus BookingStatus { get; set; }
    public Guid DriverId { get; set; }
    public DateTime ConfirmedAt { get; set; }
    public string Condition { get; set; } = string.Empty;
}

// NXP-049: DTO Gán Tài nguyên cho Booking
public class AssignBookingResourcesDto
{
    public Guid DriverId { get; set; }
    public string? DriverName { get; set; }
    public Guid TruckId { get; set; }
    public string? VehiclePlate { get; set; }
    public List<Guid> ContainerIds { get; set; } = new();
    public List<BookingContainerSelectionDto> ContainerSelections { get; set; } = new();
    public string? ContainerNo { get; set; }
}

// NXP-048: DTO Kho dữ liệu sẵn sàng phục vụ AI Auto-Match & Gợi ý tối ưu
public class AvailableFleetResourcesDto
{
    public List<EligibleContainerDto> Containers { get; set; } = new();
    public List<AvailableTruckDto> Trucks { get; set; } = new();
    public List<AvailableDriverDto> Drivers { get; set; } = new();
}

public class EligibleContainerDto
{
    public Guid Id { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string SealNumber { get; set; } = string.Empty;
    public string Size { get; set; } = "ft20";
    public string Category { get; set; } = "dry";
    public string CargoType { get; set; } = "general";
    public decimal GrossWeightKg { get; set; }
    public decimal GrossWeightTon => Math.Round(GrossWeightKg / 1000m, 2);
    public string Status { get; set; } = "in_yard";
}

public class AvailableTruckDto : BookingVehicleDetailsDto
{
    public decimal MaxPayloadTon { get; set; }
}

public class AvailableDriverDto : BookingDriverDetailsDto
{
    public string LicenseClass { get; set; } = "FC";
}

// NXP-048: AI Recommendation DTO processed completely in Backend
public class FleetRecommendationDto
{
    public Guid? RecommendedTruckId { get; set; }
    public string? RecommendedTruckPlate { get; set; }
    public decimal TruckMaxPayloadTon { get; set; }
    public Guid? RecommendedDriverId { get; set; }
    public string? RecommendedDriverName { get; set; }
    public string? DriverLicense { get; set; }
    public string? DriverPhone { get; set; }
    public Guid ContainerId { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public decimal ContainerGrossWeightTon { get; set; }
    public string ContainerSize { get; set; } = "ft20";
    public string CargoType { get; set; } = "general";
    public decimal PayloadRatio { get; set; }
    public string PayloadStatus { get; set; } = "Optimal"; // Optimal | Underutilized | Overloaded
    public string PayloadSeverity { get; set; } = "optimal"; // optimal | warning | danger
    public string PayloadMessage { get; set; } = string.Empty;
    public string RecommendedDate { get; set; } = string.Empty;
    public string RecommendedStartTime { get; set; } = "08:30";
    public string RecommendedEndTime { get; set; } = "10:30";
    public string SlotCongestionStatus { get; set; } = "Low (Thấp điểm - Khuyến nghị)";
    public string SlotAdvice { get; set; } = "Khung giờ vàng giảm tải cổng và tiết kiệm 15% phí nâng hạ.";
}

// NXP-048: Real-time Payload Evaluation DTO from Backend
public class PayloadEvaluationDto
{
    public Guid? ContainerId { get; set; }
    public Guid? TruckId { get; set; }
    public decimal ContainerGrossWeightTon { get; set; }
    public decimal TruckMaxPayloadTon { get; set; }
    public decimal PayloadRatio { get; set; }
    public string Status { get; set; } = "Optimal"; // Optimal | Underutilized | Overloaded
    public string Severity { get; set; } = "optimal"; // optimal | warning | danger
    public string WarningMessage { get; set; } = string.Empty;
    public bool IsSafe { get; set; } = true;
}

public class BookingPaymentInfoDto
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = "Unpaid";
    public decimal HandlingFee { get; set; } = 450000m;
    public decimal WeighingFee { get; set; } = 50000m;
    public decimal InfrastructureFee { get; set; } = 40000m;
    public decimal Subtotal { get; set; } = 540000m;
    public decimal TaxAmount { get; set; } = 43200m;
    public decimal TotalAmount { get; set; } = 583200m;
    public string? InvoiceNo { get; set; }
    public string? TransactionRef { get; set; }
    public string? PaymentMethod { get; set; } = "VietQR";
    public DateTime? PaidAt { get; set; }
    public string BankName { get; set; } = "MB Bank (Ngân Hàng TMCP Quân Đội)";
    public string AccountNo { get; set; } = "190388668899";
    public string AccountName { get; set; } = "CONG TY CP CANG QUOC TE NEXUSPORT";
    public string TransferContent { get; set; } = string.Empty;
    public string QrUrl { get; set; } = string.Empty;
}

public class ProcessBookingPaymentDto
{
    public string? Method { get; set; } = "vietqr";
    public string? TransactionRef { get; set; }
    public decimal? Amount { get; set; }
    public string? Note { get; set; }
}



public sealed class BookingSelectionRecommendationDto
{
    public Guid? BookingId { get; set; }
    public Guid ContainerId { get; set; }
    public Guid? DeclarationId { get; set; }
    public string BookingType { get; set; } = "Dropoff";
    public string? YardCode { get; set; }
}

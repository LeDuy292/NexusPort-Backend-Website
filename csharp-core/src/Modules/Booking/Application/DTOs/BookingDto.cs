using NexusPort.Modules.Booking.Domain.Enums;

namespace NexusPort.Modules.Booking.Application.DTOs;

public class BookingDto
{
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
    public List<string> ContainerNumbers { get; set; } = new();
}

public class CreateBookingDto
{
    public Guid CarrierId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string BookingNumber { get => BookingCode; set => BookingCode = value; }
    public BookingType BookingType { get; set; } = BookingType.Pickup;
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public List<Guid> ContainerIds { get; set; } = new();

    public string? Status { get; set; } = "Active";
    public string? Description { get; set; }
    public string? VehiclePlate { get; set; }
    public Guid? VehicleId { get; set; }
    public string? DriverName { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? GateType { get; set; } = "GateIn";
}

public class UpdateBookingDto
{
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public List<Guid> ContainerIds { get; set; } = new();
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

public class AvailableTruckDto
{
    public Guid Id { get; set; }
    public string PlateNumber { get; set; } = string.Empty;
    public string VehicleType { get; set; } = "Truck 24T";
    public decimal MaxPayloadTon { get; set; } = 24m;
    public string Status { get; set; } = "active";
    public Guid? DriverId { get; set; }
}

public class AvailableDriverDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string LicenseClass { get; set; } = "FC";
    public string Phone { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
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


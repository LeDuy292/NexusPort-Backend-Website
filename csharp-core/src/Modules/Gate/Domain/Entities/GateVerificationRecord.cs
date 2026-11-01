using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Gate.Domain.Entities;

public class GateVerificationRecord : BaseEntity, IAggregateRoot
{
    public string VerificationCode { get; set; } = string.Empty;
    public string GateCode { get; set; } = string.Empty;
    public string? LaneCode { get; set; }
    public string VerificationType { get; set; } = "AI_GATE_IN"; // AI_GATE_IN, AI_GATE_OUT, MANUAL
    public string VerificationStatus { get; set; } = "PASS"; // PASS, FAIL, MANUAL_REVIEW
    public string? FailureReason { get; set; } // BOOKING_NOT_FOUND, VEHICLE_MISMATCH, BOOKING_EXPIRED, BOOKING_INVALID_STATUS
    public DateTime VerificationTime { get; set; } = DateTime.UtcNow;

    // Recognition Results (AI Camera / YOLO / RFID Sensor)
    public string DetectedPlate { get; set; } = string.Empty;
    public double? PlateConfidence { get; set; }
    public string? RfidTag { get; set; }
    public bool VehicleDetected { get; set; } = true;
    public string? CameraId { get; set; }
    public string? OcrRawData { get; set; }

    // Associated Entities
    public Guid? BookingId { get; set; }
    public string? BookingNumber { get; set; }

    public Guid? VehicleId { get; set; }
    public string? VehiclePlate { get; set; }

    public Guid? DriverId { get; set; }
    public string? DriverName { get; set; }

    // Image Evidence
    public string? VehiclePlateImageUrl { get; set; }
    public string? OverviewImageUrl { get; set; }

    // Additional info
    public string? Notes { get; set; }
    public string? ProcessedBy { get; set; }

    public GateVerificationRecord() { }
}

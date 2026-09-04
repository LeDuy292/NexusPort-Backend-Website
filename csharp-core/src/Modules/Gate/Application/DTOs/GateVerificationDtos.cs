using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexusPort.Modules.Gate.Application.DTOs;

/// <summary>
/// Gate Recognition Event gửi từ hệ thống AI Camera / YOLO / Cảm biến IoT / RFID tại cổng
/// </summary>
public class GateRecognitionEventDto
{
    private string _gateId = string.Empty;
    private string _vehiclePlate = string.Empty;
    private string? _laneId;
    private DateTime? _recognizedAt;

    [Required(ErrorMessage = "Mã cổng (gateId) là bắt buộc")]
    [JsonPropertyName("gateId")]
    public string GateId 
    { 
        get => _gateId; 
        set => _gateId = value; 
    }

    [JsonPropertyName("gateCode")]
    public string GateCode 
    { 
        get => _gateId; 
        set => _gateId = value; 
    }

    [JsonPropertyName("laneId")]
    public string? LaneId 
    { 
        get => _laneId; 
        set => _laneId = value; 
    }

    [JsonPropertyName("laneCode")]
    public string? LaneCode 
    { 
        get => _laneId; 
        set => _laneId = value; 
    }

    [Required(ErrorMessage = "Biển số xe nhận diện (vehiclePlate) là bắt buộc")]
    [JsonPropertyName("vehiclePlate")]
    public string VehiclePlate 
    { 
        get => _vehiclePlate; 
        set => _vehiclePlate = value; 
    }

    [JsonPropertyName("detectedVehiclePlate")]
    public string DetectedVehiclePlate 
    { 
        get => _vehiclePlate; 
        set => _vehiclePlate = value; 
    }

    [JsonPropertyName("rfidTag")]
    public string? RfidTag { get; set; }

    [JsonPropertyName("vehicleDetected")]
    public bool VehicleDetected { get; set; } = true;

    [JsonPropertyName("recognizedAt")]
    public DateTime? RecognizedAt 
    { 
        get => _recognizedAt; 
        set => _recognizedAt = value; 
    }

    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp 
    { 
        get => _recognizedAt; 
        set => _recognizedAt = value; 
    }

    [JsonPropertyName("plateConfidence")]
    public double? PlateConfidence { get; set; }

    [JsonPropertyName("bookingNumber")]
    public string? BookingNumber { get; set; }

    [JsonPropertyName("cameraId")]
    public string? CameraId { get; set; }

    [JsonPropertyName("verificationType")]
    public string? VerificationType { get; set; } = "AI_GATE_IN";

    [JsonPropertyName("vehiclePlateImageUrl")]
    public string? VehiclePlateImageUrl { get; set; }

    [JsonPropertyName("overviewImageUrl")]
    public string? OverviewImageUrl { get; set; }

    [JsonPropertyName("ocrRawData")]
    public string? OcrRawData { get; set; }
}

/// <summary>
/// Alias class cho GateRecognitionEventDto để tương thích với các API hiện tại
/// </summary>
public class GateVerificationRequestDto : GateRecognitionEventDto
{
}

/// <summary>
/// Kết quả phản hồi xác thực tại cổng (PASS / FAIL)
/// </summary>
public class GateVerificationResultDto
{
    public Guid RecordId { get; set; }
    public string VerificationCode { get; set; } = string.Empty;
    public string Status { get; set; } = "PASS"; // PASS or FAIL
    public bool IsSuccess => Status == "PASS";
    public string Message { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public DateTime VerificationTime { get; set; }

    public string GateCode { get; set; } = string.Empty;
    public string? LaneCode { get; set; }

    public string DetectedVehiclePlate { get; set; } = string.Empty;
    public double? PlateConfidence { get; set; }
    public string? RfidTag { get; set; }
    public bool VehicleDetected { get; set; }

    public GateVerificationBookingInfo? Booking { get; set; }
    public GateVerificationEvidenceInfo? ImageEvidence { get; set; }
}

public class GateVerificationBookingInfo
{
    public Guid? BookingId { get; set; }
    public string? BookingNumber { get; set; }
    public string? Status { get; set; }
    public string? ExpectedVehiclePlate { get; set; }
    public string? DriverName { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? GateType { get; set; }
}

public class GateVerificationEvidenceInfo
{
    public string? VehiclePlateImageUrl { get; set; }
    public string? OverviewImageUrl { get; set; }
}

/// <summary>
/// DTO chi tiết bản ghi lịch sử xác thực cổng
/// </summary>
public class GateVerificationRecordDto
{
    public Guid Id { get; set; }
    public string VerificationCode { get; set; } = string.Empty;
    public string GateCode { get; set; } = string.Empty;
    public string? LaneCode { get; set; }
    public string VerificationType { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public DateTime VerificationTime { get; set; }

    public string DetectedPlate { get; set; } = string.Empty;
    public double? PlateConfidence { get; set; }
    public string? RfidTag { get; set; }
    public bool VehicleDetected { get; set; }
    public string? CameraId { get; set; }
    public string? OcrRawData { get; set; }

    public Guid? BookingId { get; set; }
    public string? BookingNumber { get; set; }

    public Guid? VehicleId { get; set; }
    public string? VehiclePlate { get; set; }

    public Guid? DriverId { get; set; }
    public string? DriverName { get; set; }

    public string? VehiclePlateImageUrl { get; set; }
    public string? OverviewImageUrl { get; set; }

    public string? Notes { get; set; }
    public string? ProcessedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Bộ lọc tra cứu lịch sử xác thực cổng
/// </summary>
public class GateVerificationFilterDto
{
    public string? GateCode { get; set; }
    public string? VerificationStatus { get; set; } // PASS, FAIL, MANUAL_REVIEW
    public string? PlateNumber { get; set; }
    public string? RfidTag { get; set; }
    public string? BookingNumber { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// Xử lý ghi đè thủ công từ nhân viên trực cổng (Gate Officer)
/// </summary>
public class ManualOverrideDto
{
    [Required]
    public bool Approved { get; set; }

    [Required]
    public string Reason { get; set; } = string.Empty;

    public string? OfficerId { get; set; }
}

/// <summary>
/// Yêu cầu kiểm tra trước (Pre-check) điều kiện Gate-In / Gate-Out qua Rule Engine
/// </summary>
public class GateRulePreCheckRequestDto
{
    [Required(ErrorMessage = "Loại cổng (GateType: GateIn hoặc GateOut) là bắt buộc")]
    public string GateType { get; set; } = "GateIn";

    [Required(ErrorMessage = "Biển số xe (VehiclePlate) là bắt buộc")]
    public string VehiclePlate { get; set; } = string.Empty;

    public string? RfidTag { get; set; }
    public bool VehicleDetected { get; set; } = true;
    public string? GateCode { get; set; } = "GATE-01";
    public string? LaneCode { get; set; }
    public string? DriverName { get; set; }
    public string? ContainerNumber { get; set; }
    public string? BookingNumber { get; set; }
    public DateTime? VerificationTime { get; set; }
}

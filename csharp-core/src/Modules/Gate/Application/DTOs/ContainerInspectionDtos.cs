using System.ComponentModel.DataAnnotations;

namespace NexusPort.Modules.Gate.Application.DTOs;

public class ContainerBoundingBoxDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class ContainerAiDetectionDto
{
    public int ClassId { get; set; }
    public string Label { get; set; } = string.Empty; // "ID" hoặc "Type"
    public double Confidence { get; set; }
    public ContainerBoundingBoxDto Box { get; set; } = new();
    public string? ExtractedText { get; set; }
    public string? CroppedImageBase64 { get; set; }
}

public class ContainerModelStatusDto
{
    public bool IsModelLoaded { get; set; }
    public string? ModelPath { get; set; }
    public long? ModelFileSizeBytes { get; set; }
    public string InputName { get; set; } = "images";
    public int[] InputShape { get; set; } = [1, 3, 640, 640];
    public string OutputName { get; set; } = "output0";
    public int[] OutputShape { get; set; } = [1, 6, 8400];
    public Dictionary<int, string> Labels { get; set; } = new() { [0] = "ID", [1] = "Type" };
    public bool AzureOcrConfigured { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class ContainerInspectImageBase64RequestDto
{
    [Required(ErrorMessage = "Vui lòng cung cấp ảnh container (imageBase64).")]
    public string ImageBase64 { get; set; } = string.Empty;

    public string? BookingNumber { get; set; }
    public string? VehiclePlate { get; set; }
    public string? GateCode { get; set; } = "GATE-01";
    public string? LaneCode { get; set; } = "LANE_01";
    public string? GateType { get; set; } = "GateIn";
    public bool AutoVerify { get; set; } = false;
}

public class ContainerInspectionQuery
{
    public string? BookingNumber { get; set; }
    public string? VehiclePlate { get; set; }
    public string? GateCode { get; set; } = "GATE-01";
    public string? LaneCode { get; set; } = "LANE_01";
    public string? GateType { get; set; } = "GateIn";
    public bool AutoVerify { get; set; } = false;
    public string? OverviewImageUrl { get; set; }
}

public class ExpectedBookingContainerInfoDto
{
    public Guid? ContainerId { get; set; }
    public Guid? BookingOperationId { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string? IsoSize { get; set; }
    public string? DbStatus { get; set; }
    public string? SealNumber { get; set; }
    public string? OperationType { get; set; }
    public string? ExpectedGateDirection { get; set; }
    public string? VerificationStatus { get; set; }
}

public class ContainerInspectionResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public double ProcessingTimeMs { get; set; }
    public bool ModelLoaded { get; set; }
    public string OcrProvider { get; set; } = "None";

    public string? DetectedContainerNumber { get; set; }
    public string? RawIdText { get; set; }
    public double IdConfidence { get; set; }
    public bool Iso6346Valid { get; set; }
    public string? Iso6346Error { get; set; }

    public string? DetectedContainerType { get; set; }
    public string? RawTypeText { get; set; }
    public double TypeConfidence { get; set; }

    public string? AnnotatedImageBase64 { get; set; }
    public IReadOnlyList<ContainerAiDetectionDto> Detections { get; set; } = Array.Empty<ContainerAiDetectionDto>();

    // Kết quả đối chiếu nghiệp vụ Gate & Booking
    public Guid? BookingId { get; set; }
    public string? BookingNumber { get; set; }
    public string? BookingStatus { get; set; }
    public Guid? ContainerEntityId { get; set; }
    public string? ContainerDbStatus { get; set; }
    public string? ExpectedIsoSizeType { get; set; }
    public bool? IsMatchedWithBooking { get; set; }
    public bool? IsTypeCompatible { get; set; }
    public string InspectionDecision { get; set; } = "REVIEW_REQUIRED"; // PASS, FAIL, REVIEW_REQUIRED
    public string DecisionReason { get; set; } = string.Empty;
    public IReadOnlyList<ExpectedBookingContainerInfoDto> ExpectedContainers { get; set; } = Array.Empty<ExpectedBookingContainerInfoDto>();

    public GateVerificationResultDto? GateVerificationResult { get; set; }
}

public class ContainerVerifyDecisionRequestDto
{
    public Guid? BookingId { get; set; }
    public string? BookingNumber { get; set; }
    public Guid? BookingOperationId { get; set; }

    [Required(ErrorMessage = "Số container kiểm duyệt là bắt buộc.")]
    public string ContainerNumber { get; set; } = string.Empty;

    public string? DetectedContainerType { get; set; }
    public double? Confidence { get; set; }
    public string? VehiclePlate { get; set; }
    public string GateCode { get; set; } = "GATE-01";
    public string? LaneCode { get; set; } = "LANE_01";
    public string GateType { get; set; } = "GateIn";
    public bool Approved { get; set; } = true;
    public string? Notes { get; set; }
    public string? OverviewImageUrl { get; set; }
    public string? ContainerCropImageUrl { get; set; }
}

public class ContainerVerifyDecisionResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = "Verified"; // Verified | Rejected
    public Guid? BookingId { get; set; }
    public string? BookingNumber { get; set; }
    public Guid? BookingOperationId { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public bool Iso6346Valid { get; set; }
    public Guid? VerificationRecordId { get; set; }
    public string? VerificationCode { get; set; }
    public GateVerificationResultDto? GateVerificationResult { get; set; }
}


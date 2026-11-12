using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Domain.Entities;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Modules.Gate.Application.DTOs;
using NexusPort.Modules.Gate.Application.Interfaces;
using NexusPort.Modules.Gate.Domain.Entities;
using NexusPort.Modules.Gate.Domain.Rules;
using NexusPort.Modules.Gate.Domain.Rules.Concrete;
using NexusPort.Modules.Gate.Domain.Validation;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Gate.Application.Services;

/// <summary>
/// Service điều phối kiểm duyệt container bằng mô hình AI container.onnx kết hợp OCR, ISO 6346 và đối chiếu Booking/Gate.
/// </summary>
public class GateContainerInspectionService : IGateContainerInspectionService
{
    private static readonly BookingStatus[] InactiveBookingStatuses =
    [
        BookingStatus.Rejected,
        BookingStatus.Canceled,
        BookingStatus.Expired,
        BookingStatus.Completed
    ];

    private readonly IContainerDetectionService _detectionService;
    private readonly IGateVerificationService _gateVerificationService;
    private readonly IGateVerificationRepository _verificationRepository;
    private readonly AppDbContext _context;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<GateContainerInspectionService> _logger;

    public GateContainerInspectionService(
        IContainerDetectionService detectionService,
        IGateVerificationService gateVerificationService,
        IGateVerificationRepository verificationRepository,
        AppDbContext context,
        ICurrentUser currentUser,
        ILogger<GateContainerInspectionService> logger)
    {
        _detectionService = detectionService;
        _gateVerificationService = gateVerificationService;
        _verificationRepository = verificationRepository;
        _context = context;
        _currentUser = currentUser;
        _logger = logger;
    }

    public ContainerModelStatusDto GetModelStatus() => _detectionService.GetModelStatus();

    public async Task<ContainerInspectionResultDto> InspectImageAsync(
        byte[] imageBytes,
        ContainerInspectionQuery query,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes == null || imageBytes.Length == 0)
        {
            throw new ValidationException("Image", "Dữ liệu ảnh container trống hoặc không hợp lệ.");
        }

        var gateType = GateBookingContainerHelper.NormalizeGateType(query.GateType);
        var aiResult = await _detectionService.DetectAsync(imageBytes, cancellationToken);
        var detectedContainerNo = Iso6346Validator.Normalize(aiResult.DetectedContainerNumber);

        var booking = await ResolveBookingAsync(
            query.BookingNumber,
            query.VehiclePlate,
            detectedContainerNo,
            gateType,
            cancellationToken);

        var expectedContainers = booking != null
            ? await GateBookingContainerHelper.LoadExpectedBookingContainersAsync(_context, booking.Id, gateType, cancellationToken)
            : Array.Empty<ExpectedBookingContainerInfoDto>();

        var scannedContainer = !string.IsNullOrEmpty(detectedContainerNo)
            ? await _context.Set<Container.Domain.Entities.Container>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == detectedContainerNo, cancellationToken)
            : null;

        var (expectedNumbers, expectedIsoSizes) = GateBookingContainerHelper.ExtractExpectations(expectedContainers);

        var (decision, reason, isMatched, isTypeCompatible) = await EvaluateDecisionAsync(
            aiResult,
            detectedContainerNo,
            gateType,
            booking,
            scannedContainer,
            expectedNumbers,
            expectedIsoSizes,
            cancellationToken);

        GateVerificationResultDto? gateVerificationResult = null;
        if (query.AutoVerify && !string.IsNullOrWhiteSpace(query.VehiclePlate) && !string.IsNullOrEmpty(detectedContainerNo))
        {
            gateVerificationResult = await TriggerAutoGateVerificationAsync(
                query,
                gateType,
                booking,
                aiResult,
                detectedContainerNo,
                cancellationToken);
        }

        return new ContainerInspectionResultDto
        {
            Success = aiResult.ModelLoaded,
            Message = string.IsNullOrEmpty(detectedContainerNo)
                ? (aiResult.ErrorMessage ?? "Đã chạy nhận diện ảnh container nhưng chưa trích xuất được số container.")
                : "Kiểm duyệt container bằng AI container.onnx hoàn tất.",
            ProcessingTimeMs = aiResult.ProcessingTimeMs,
            ModelLoaded = aiResult.ModelLoaded,
            OcrProvider = aiResult.OcrProvider,
            DetectedContainerNumber = string.IsNullOrEmpty(detectedContainerNo) ? null : detectedContainerNo,
            RawIdText = aiResult.RawIdText,
            IdConfidence = aiResult.IdConfidence,
            Iso6346Valid = aiResult.Iso6346Valid,
            Iso6346Error = aiResult.Iso6346Error,
            DetectedContainerType = aiResult.DetectedContainerType,
            RawTypeText = aiResult.RawTypeText,
            TypeConfidence = aiResult.TypeConfidence,
            AnnotatedImageBase64 = aiResult.AnnotatedImageBase64,
            Detections = aiResult.Detections,
            BookingId = booking?.Id,
            BookingNumber = booking?.BookingCode,
            BookingStatus = booking?.Status.ToString(),
            ContainerEntityId = scannedContainer?.Id,
            ContainerDbStatus = scannedContainer?.Status,
            ExpectedIsoSizeType = expectedIsoSizes.Count > 0 ? string.Join(", ", expectedIsoSizes) : null,
            IsMatchedWithBooking = isMatched,
            IsTypeCompatible = isTypeCompatible,
            InspectionDecision = decision,
            DecisionReason = reason,
            ExpectedContainers = expectedContainers,
            GateVerificationResult = gateVerificationResult
        };
    }

    public async Task<ContainerVerifyDecisionResultDto> RecordContainerDecisionAsync(
        ContainerVerifyDecisionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var containerNo = Iso6346Validator.Normalize(request.ContainerNumber);
        if (string.IsNullOrEmpty(containerNo))
        {
            throw new ValidationException("ContainerNumber", "Số container kiểm duyệt không được để trống.");
        }

        var isoCheck = Iso6346Validator.Validate(containerNo);
        if (request.Approved && !isoCheck.IsValid)
        {
            throw new ValidationException("ContainerNumber", isoCheck.ErrorMessage ?? $"Số container '{containerNo}' không hợp lệ theo chuẩn ISO 6346.");
        }

        var gateType = GateBookingContainerHelper.NormalizeGateType(request.GateType);
        var booking = await ResolveBookingForDecisionAsync(request, containerNo, gateType, cancellationToken);
        var operation = await FindTargetOperationAsync(booking?.Id, request.BookingOperationId, containerNo, gateType, cancellationToken);

        if (operation != null)
        {
            operation.VerificationStatus = request.Approved ? "Verified" : "Rejected";
            operation.VerifiedBy = _currentUser.UserId;
            operation.VerifiedAt = DateTime.UtcNow;
            operation.VerificationNotes = string.IsNullOrWhiteSpace(request.Notes)
                ? (request.Approved ? $"Gate kiểm duyệt đạt ({containerNo})" : $"Gate từ chối container ({containerNo})")
                : request.Notes.Trim();
            operation.ContainerCondition = request.Approved ? "Good" : "InspectionRejected";
            if (string.IsNullOrWhiteSpace(operation.ContainerNumber))
            {
                operation.ContainerNumber = containerNo;
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        var rawPayload = JsonSerializer.Serialize(new
        {
            source = "CONTAINER_ONNX_DECISION",
            containerNumber = containerNo,
            containerType = request.DetectedContainerType,
            confidence = request.Confidence,
            iso6346Valid = isoCheck.IsValid,
            approved = request.Approved,
            notes = request.Notes,
            bookingOperationId = operation?.Id ?? request.BookingOperationId
        });

        GateVerificationResultDto? gateResult = null;
        Guid recordId;
        string verificationCode;

        if (!string.IsNullOrWhiteSpace(request.VehiclePlate))
        {
            gateResult = await _gateVerificationService.VerifyGateScanAsync(new GateRecognitionEventDto
            {
                GateCode = string.IsNullOrWhiteSpace(request.GateCode) ? "GATE-01" : request.GateCode,
                LaneCode = string.IsNullOrWhiteSpace(request.LaneCode) ? "LANE_01" : request.LaneCode,
                VehiclePlate = request.VehiclePlate.Trim().ToUpperInvariant(),
                BookingNumber = booking?.BookingCode ?? request.BookingNumber,
                ContainerNumber = containerNo,
                DetectedContainerType = request.DetectedContainerType,
                ContainerConfidence = request.Confidence,
                FromContainerOnnx = true,
                VerificationType = gateType == "GateOut" ? "CONTAINER_OUT" : "CONTAINER_IN",
                OverviewImageUrl = request.OverviewImageUrl,
                ContainerImageUrl = request.ContainerCropImageUrl,
                ManualReason = request.Approved ? null : request.Notes,
                OcrRawData = rawPayload
            }, cancellationToken);

            recordId = gateResult.RecordId;
            verificationCode = gateResult.VerificationCode;
        }
        else
        {
            verificationCode = $"GVR-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
            var record = new GateVerificationRecord
            {
                VerificationCode = verificationCode,
                GateCode = string.IsNullOrWhiteSpace(request.GateCode) ? "GATE-01" : request.GateCode.Trim().ToUpperInvariant(),
                LaneCode = request.LaneCode?.Trim().ToUpperInvariant(),
                VerificationType = gateType == "GateOut" ? "CONTAINER_OUT" : "CONTAINER_IN",
                VerificationStatus = request.Approved ? "PASS" : "FAIL",
                FailureReason = request.Approved ? null : (request.Notes ?? "Gate Officer từ chối container khi kiểm duyệt."),
                VerificationTime = DateTime.UtcNow,
                DetectedPlate = string.Empty,
                VehicleDetected = false,
                BookingId = booking?.Id,
                BookingNumber = booking?.BookingCode ?? request.BookingNumber,
                OverviewImageUrl = request.OverviewImageUrl,
                VehiclePlateImageUrl = request.ContainerCropImageUrl,
                Notes = BuildAuditNotes(containerNo, request.DetectedContainerType, request.Notes),
                ProcessedBy = _currentUser.Username ?? _currentUser.Email ?? "GATE_OFFICER",
                OcrRawData = rawPayload
            };

            await _verificationRepository.AddAsync(record, cancellationToken);
            recordId = record.Id;
        }

        _logger.LogInformation(
            "Gate container inspection decision recorded: Container={ContainerNo}, Approved={Approved}, Booking={BookingCode}, Operation={OperationId}, Record={VerificationCode}",
            containerNo,
            request.Approved,
            booking?.BookingCode,
            operation?.Id,
            verificationCode);

        return new ContainerVerifyDecisionResultDto
        {
            Success = true,
            Message = request.Approved
                ? $"Đã xác nhận kiểm duyệt container {containerNo} thành công."
                : $"Đã ghi nhận từ chối container {containerNo}.",
            VerificationStatus = request.Approved ? "Verified" : "Rejected",
            BookingId = booking?.Id,
            BookingNumber = booking?.BookingCode ?? request.BookingNumber,
            BookingOperationId = operation?.Id ?? request.BookingOperationId,
            ContainerNumber = containerNo,
            Iso6346Valid = isoCheck.IsValid,
            VerificationRecordId = recordId,
            VerificationCode = verificationCode,
            GateVerificationResult = gateResult
        };
    }

    private async Task<Booking.Domain.Entities.Booking?> ResolveBookingAsync(
        string? bookingNumber,
        string? vehiclePlate,
        string detectedContainerNo,
        string gateType,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(bookingNumber))
        {
            var normalizedCode = bookingNumber.Trim().ToUpperInvariant();
            return await _context.Set<Booking.Domain.Entities.Booking>()
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.BookingCode.ToUpper() == normalizedCode, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(vehiclePlate))
        {
            var resolvedId = await new GateEntryAccess(_context).ResolveBookingAsync(
                vehiclePlate.Trim().ToUpperInvariant(),
                cancellationToken,
                gateType == "GateOut");

            if (resolvedId.HasValue)
            {
                return await _context.Set<Booking.Domain.Entities.Booking>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b => b.Id == resolvedId.Value, cancellationToken);
            }
        }

        if (string.IsNullOrEmpty(detectedContainerNo))
        {
            return null;
        }

        // Reverse lookup qua BookingOperation đang active có số container trùng khớp
        var operationMatch = await (
            from op in _context.Set<BookingOperation>().AsNoTracking()
            join b in _context.Set<Booking.Domain.Entities.Booking>().AsNoTracking() on op.BookingId equals b.Id
            where op.ContainerNumber != null
                  && op.ContainerNumber.ToUpper() == detectedContainerNo
                  && !InactiveBookingStatuses.Contains(b.Status)
            orderby (op.ExpectedGateDirection == gateType ? 0 : 1), b.CreatedAt descending
            select b)
            .FirstOrDefaultAsync(cancellationToken);

        if (operationMatch != null)
        {
            return operationMatch;
        }

        // Fallback qua bảng BookingContainer + Container
        return await (
            from bc in _context.Set<BookingContainer>().AsNoTracking()
            join c in _context.Set<Container.Domain.Entities.Container>().AsNoTracking() on bc.ContainerId equals c.Id
            join b in _context.Set<Booking.Domain.Entities.Booking>().AsNoTracking() on bc.BookingId equals b.Id
            where c.ContainerNumber.ToUpper() == detectedContainerNo
                  && !InactiveBookingStatuses.Contains(b.Status)
            orderby b.CreatedAt descending
            select b)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Booking.Domain.Entities.Booking?> ResolveBookingForDecisionAsync(
        ContainerVerifyDecisionRequestDto request,
        string containerNo,
        string gateType,
        CancellationToken cancellationToken)
    {
        if (request.BookingId.HasValue)
        {
            return await _context.Set<Booking.Domain.Entities.Booking>()
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == request.BookingId.Value, cancellationToken);
        }

        if (request.BookingOperationId.HasValue)
        {
            var opBookingId = await _context.Set<BookingOperation>()
                .AsNoTracking()
                .Where(op => op.Id == request.BookingOperationId.Value)
                .Select(op => (Guid?)op.BookingId)
                .FirstOrDefaultAsync(cancellationToken);

            if (opBookingId.HasValue)
            {
                return await _context.Set<Booking.Domain.Entities.Booking>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b => b.Id == opBookingId.Value, cancellationToken);
            }
        }

        return await ResolveBookingAsync(request.BookingNumber, request.VehiclePlate, containerNo, gateType, cancellationToken);
    }

    private async Task<(string Decision, string Reason, bool? IsMatched, bool? IsTypeCompatible)> EvaluateDecisionAsync(
        ContainerOnnxInferenceResult aiResult,
        string detectedContainerNo,
        string gateType,
        Booking.Domain.Entities.Booking? booking,
        Container.Domain.Entities.Container? scannedContainer,
        IReadOnlyList<string> expectedNumbers,
        IReadOnlyList<string> expectedIsoSizes,
        CancellationToken cancellationToken)
    {
        if (!aiResult.ModelLoaded)
        {
            return ("REVIEW_REQUIRED", aiResult.ErrorMessage ?? "Model AI ONNX chưa sẵn sàng.", null, null);
        }

        if (string.IsNullOrEmpty(detectedContainerNo))
        {
            return ("REVIEW_REQUIRED", "Chưa trích xuất được số container rõ nét từ ảnh. Gate Officer vui lòng kiểm tra lại hoặc nhập tay.", null, null);
        }

        bool? isMatched = booking != null && expectedNumbers.Count > 0
            ? expectedNumbers.Contains(detectedContainerNo, StringComparer.OrdinalIgnoreCase)
            : null;

        bool? isTypeCompatible = !string.IsNullOrWhiteSpace(aiResult.DetectedContainerType) && expectedIsoSizes.Count > 0
            ? expectedIsoSizes.Any(e => Iso6346Validator.AreIsoTypesCompatible(aiResult.DetectedContainerType, e))
            : null;

        var rule = new ContainerMatchAndStatusRule();
        var ruleContext = new GateValidationContext
        {
            GateType = gateType,
            ContainerNumber = detectedContainerNo,
            DetectedContainerType = aiResult.DetectedContainerType,
            ContainerConfidence = aiResult.IdConfidence,
            StrictContainerIsoCheck = true,
            Container = scannedContainer,
            Booking = booking,
            BookingNumber = booking?.BookingCode,
            ExpectedBookingContainerNumbers = expectedNumbers,
            ExpectedBookingIsoSizes = expectedIsoSizes
        };

        var ruleResult = await rule.EvaluateAsync(ruleContext, cancellationToken);
        if (!ruleResult.IsSuccess)
        {
            return ("FAIL", ruleResult.Message, isMatched, isTypeCompatible);
        }

        if (booking == null)
        {
            return ("REVIEW_REQUIRED",
                $"Mã container {detectedContainerNo} hợp lệ theo ISO 6346 nhưng chưa đối khớp được Booking nào trong hệ thống.",
                null,
                isTypeCompatible);
        }

        if (expectedNumbers.Count == 0)
        {
            return ("REVIEW_REQUIRED",
                $"Booking {booking.BookingCode} chưa khai báo danh sách container dự kiến để đối khớp tự động.",
                null,
                isTypeCompatible);
        }

        return ("PASS",
            $"Container {detectedContainerNo} hợp lệ chuẩn ISO 6346 và khớp với Booking {booking.BookingCode}.",
            isMatched,
            isTypeCompatible);
    }

    private async Task<GateVerificationResultDto?> TriggerAutoGateVerificationAsync(
        ContainerInspectionQuery query,
        string gateType,
        Booking.Domain.Entities.Booking? booking,
        ContainerOnnxInferenceResult aiResult,
        string detectedContainerNo,
        CancellationToken cancellationToken)
    {
        var ocrJson = JsonSerializer.Serialize(new
        {
            source = "CONTAINER_ONNX",
            containerNumber = detectedContainerNo,
            containerType = aiResult.DetectedContainerType,
            idConfidence = aiResult.IdConfidence,
            typeConfidence = aiResult.TypeConfidence,
            iso6346Valid = aiResult.Iso6346Valid,
            ocrProvider = aiResult.OcrProvider
        });

        return await _gateVerificationService.VerifyGateScanAsync(new GateRecognitionEventDto
        {
            GateCode = string.IsNullOrWhiteSpace(query.GateCode) ? "GATE-01" : query.GateCode,
            LaneCode = string.IsNullOrWhiteSpace(query.LaneCode) ? "LANE_01" : query.LaneCode,
            VehiclePlate = query.VehiclePlate!.Trim().ToUpperInvariant(),
            BookingNumber = booking?.BookingCode ?? query.BookingNumber,
            ContainerNumber = detectedContainerNo,
            DetectedContainerType = aiResult.DetectedContainerType,
            ContainerConfidence = aiResult.IdConfidence,
            FromContainerOnnx = true,
            VerificationType = gateType == "GateOut" ? "AI_GATE_OUT" : "AI_GATE_IN",
            OverviewImageUrl = query.OverviewImageUrl,
            OcrRawData = ocrJson
        }, cancellationToken);
    }

    private async Task<BookingOperation?> FindTargetOperationAsync(
        Guid? bookingId,
        Guid? bookingOperationId,
        string containerNo,
        string gateType,
        CancellationToken cancellationToken)
    {
        if (bookingOperationId.HasValue)
        {
            return await _context.Set<BookingOperation>()
                .FirstOrDefaultAsync(op => op.Id == bookingOperationId.Value, cancellationToken);
        }

        if (!bookingId.HasValue)
        {
            return null;
        }

        var ops = await _context.Set<BookingOperation>()
            .Where(op => op.BookingId == bookingId.Value)
            .ToListAsync(cancellationToken);

        return ops.FirstOrDefault(op =>
                   !string.IsNullOrWhiteSpace(op.ContainerNumber) &&
                   string.Equals(Iso6346Validator.Normalize(op.ContainerNumber), containerNo, StringComparison.OrdinalIgnoreCase))
               ?? ops.FirstOrDefault(op => string.Equals(op.ExpectedGateDirection, gateType, StringComparison.OrdinalIgnoreCase))
               ?? (ops.Count == 1 ? ops[0] : null);
    }

    private static string BuildAuditNotes(string containerNo, string? detectedType, string? userNotes)
    {
        var typePart = string.IsNullOrWhiteSpace(detectedType) ? string.Empty : $" | ISO Type: {detectedType}";
        var notePart = string.IsNullOrWhiteSpace(userNotes) ? string.Empty : $" | {userNotes.Trim()}";
        var combined = $"Kiểm duyệt Container ONNX: {containerNo}{typePart}{notePart}";
        return combined.Length > 1000 ? combined[..1000] : combined;
    }
}

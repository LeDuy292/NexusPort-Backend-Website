using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Domain.Entities;
using NexusPort.Modules.Container.Domain.Entities;
using NexusPort.Modules.Driver.Domain.Entities;
using NexusPort.Modules.Gate.Application.DTOs;
using NexusPort.Modules.Gate.Application.Interfaces;
using NexusPort.Modules.Gate.Domain.Entities;
using NexusPort.Modules.Gate.Domain.Rules;
using NexusPort.Modules.Vehicle.Domain.Entities;

using NexusPort.Infrastructure.Notifications.DTOs;
using NexusPort.Infrastructure.Notifications.Enums;
using NexusPort.Infrastructure.Notifications.Interfaces;
using NexusPort.Modules.Booking.Domain.Enums;

namespace NexusPort.Modules.Gate.Application.Services;

public class GateVerificationService : IGateVerificationService
{
    private readonly IGateVerificationRepository _verificationRepository;
    private readonly IGateRuleEngine _ruleEngine;
    private readonly AppDbContext _context;
    private readonly INotificationService? _notificationService;

    public GateVerificationService(
        IGateVerificationRepository verificationRepository,
        IGateRuleEngine ruleEngine,
        AppDbContext context,
        INotificationService? notificationService = null)
    {
        _verificationRepository = verificationRepository;
        _ruleEngine = ruleEngine;
        _context = context;
        _notificationService = notificationService;
    }

    public async Task<GateVerificationResultDto> VerifyGateScanAsync(GateRecognitionEventDto request, CancellationToken cancellationToken = default)
    {
        var verificationTime = request.Timestamp ?? DateTime.UtcNow;
        var normalizedDetectedPlate = NormalizePlate(request.DetectedVehiclePlate);

        // 1. Tìm Booking tương ứng
        Booking.Domain.Entities.Booking? booking = null;

        // 1. Tra cứu Vehicle & Driver trong DB trước theo biển số
        var vehicles = await _context.Set<Vehicle.Domain.Entities.Vehicle>().ToListAsync(cancellationToken);
        var matchedVehicle = vehicles.FirstOrDefault(v => NormalizePlate(v.PlateNumber) == normalizedDetectedPlate);
        if (matchedVehicle == null && !string.IsNullOrWhiteSpace(request.RfidTag))
        {
            matchedVehicle = vehicles.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.RfidTag) && 
                string.Equals(v.RfidTag.Trim(), request.RfidTag.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.BookingNumber))
        {
            var searchNumber = request.BookingNumber.Trim().ToUpper();
            booking = await _context.Set<Booking.Domain.Entities.Booking>()
                .FirstOrDefaultAsync(b => b.BookingCode.ToUpper() == searchNumber, cancellationToken);
        }
        else if (matchedVehicle != null)
        {
            booking = await _context.Set<Booking.Domain.Entities.Booking>()
                .FirstOrDefaultAsync(b => b.TruckId == matchedVehicle.Id && b.Status != BookingStatus.Canceled && b.Status != BookingStatus.Completed && b.Status != BookingStatus.Expired, cancellationToken);
        }

        Container.Domain.Entities.Container? container = null;
        if (!string.IsNullOrWhiteSpace(request.ContainerNumber))
        {
            var containerNo = request.ContainerNumber.Trim().ToUpper();
            container = await _context.Set<Container.Domain.Entities.Container>()
                .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == containerNo, cancellationToken);
        }

        var vehicleId = matchedVehicle?.Id ?? booking?.VehicleId;
        var driverId = booking?.DriverId;
        var driverName = booking?.DriverName;

        Driver.Domain.Entities.Driver? driver = null;
        if (driverId.HasValue)
        {
            driver = await _context.Set<Driver.Domain.Entities.Driver>()
                .FirstOrDefaultAsync(d => d.Id == driverId.Value, cancellationToken);
            if (string.IsNullOrWhiteSpace(driverName))
            {
                driverName = driver?.FullName;
            }
        }

        // 4. Khởi tạo GateValidationContext và ủy quyền kiểm tra cho Rule Engine
        var gateType = request.VerificationType?.Contains("OUT", StringComparison.OrdinalIgnoreCase) == true ? "GateOut" : "GateIn";
        var validationContext = new GateValidationContext
        {
            GateType = gateType,
            GateCode = request.GateCode.Trim().ToUpper(),
            LaneCode = request.LaneCode?.Trim().ToUpper(),
            VerificationTime = verificationTime,
            VehiclePlate = request.DetectedVehiclePlate.Trim().ToUpper(),
            RfidTag = request.RfidTag,
            VehicleDetected = request.VehicleDetected,
            Vehicle = matchedVehicle,
            DriverName = driverName,
            Driver = driver,
            ContainerNumber = request.ContainerNumber,
            Container = container,
            BookingNumber = request.BookingNumber,
            Booking = booking,
            OperationType = request.OperationType,
            DriverConfirmed = request.DriverConfirmed,
            BillingSettled = request.BillingSettled,
            BillingStatus = request.BillingStatus
        };

        var ruleEvalResult = await _ruleEngine.EvaluateAsync(validationContext, cancellationToken);

        var status = ruleEvalResult.Status;
        var failureReason = ruleEvalResult.FailureReason;
        var message = ruleEvalResult.Message;

        // 4. Khởi tạo và lưu GateVerificationRecord vào DB
        var verificationCode = $"GVR-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpper()}";
        var record = new GateVerificationRecord
        {
            VerificationCode = verificationCode,
            GateCode = request.GateCode.Trim().ToUpper(),
            LaneCode = request.LaneCode?.Trim().ToUpper(),
            VerificationType = string.IsNullOrWhiteSpace(request.VerificationType) ? "AI_GATE_IN" : request.VerificationType,
            VerificationStatus = status,
            FailureReason = failureReason,
            VerificationTime = verificationTime,

            DetectedPlate = request.DetectedVehiclePlate.Trim().ToUpper(),
            PlateConfidence = request.PlateConfidence,
            RfidTag = request.RfidTag,
            VehicleDetected = request.VehicleDetected,
            CameraId = request.CameraId,
            OcrRawData = request.OcrRawData,

            BookingId = booking?.Id,
            BookingNumber = booking?.BookingNumber,

            VehicleId = vehicleId,
            VehiclePlate = booking?.VehiclePlate ?? request.DetectedVehiclePlate.Trim().ToUpper(),

            DriverId = driverId,
            DriverName = driverName,

            VehiclePlateImageUrl = request.VehiclePlateImageUrl,
            OverviewImageUrl = request.OverviewImageUrl,

            Notes = message,
            ProcessedBy = "AI_YOLO_SYSTEM"
        };

        await _verificationRepository.AddAsync(record, cancellationToken);

        // 5. Chuẩn bị kết quả trả về
        return new GateVerificationResultDto
        {
            RecordId = record.Id,
            VerificationCode = record.VerificationCode,
            Status = status,
            Message = message,
            FailureReason = failureReason,
            VerificationTime = verificationTime,
            GateCode = record.GateCode,
            LaneCode = record.LaneCode,
            DetectedVehiclePlate = record.DetectedPlate,
            PlateConfidence = record.PlateConfidence,
            RfidTag = record.RfidTag,
            VehicleDetected = record.VehicleDetected,
            Booking = booking == null ? null : new GateVerificationBookingInfo
            {
                BookingId = booking.Id,
                BookingNumber = booking.BookingNumber,
                Status = booking.Status.ToString(),
                ExpectedVehiclePlate = booking.VehiclePlate,
                DriverName = driverName,
                ValidFrom = booking.ValidFrom,
                ValidTo = booking.ValidTo,
                GateType = booking.GateType
            },
            ImageEvidence = new GateVerificationEvidenceInfo
            {
                VehiclePlateImageUrl = record.VehiclePlateImageUrl,
                OverviewImageUrl = record.OverviewImageUrl
            }
        };
    }

    public async Task<GateRuleEvaluationResult> EvaluateRulesAsync(GateRulePreCheckRequestDto request, CancellationToken cancellationToken = default)
    {
        var verificationTime = request.VerificationTime ?? DateTime.UtcNow;
        var normalizedPlate = NormalizePlate(request.VehiclePlate);

        // 1. Tìm Vehicle
        var vehicles = await _context.Set<Vehicle.Domain.Entities.Vehicle>().ToListAsync(cancellationToken);
        var vehicle = vehicles.FirstOrDefault(v => NormalizePlate(v.PlateNumber) == normalizedPlate);

        // 2. Tìm Booking
        Booking.Domain.Entities.Booking? booking = null;
        if (!string.IsNullOrWhiteSpace(request.BookingNumber))
        {
            var searchNumber = request.BookingNumber.Trim().ToUpper();
            booking = await _context.Set<Booking.Domain.Entities.Booking>()
                .FirstOrDefaultAsync(b => b.BookingCode.ToUpper() == searchNumber, cancellationToken);
        }
        else if (vehicle != null)
        {
            booking = await _context.Set<Booking.Domain.Entities.Booking>()
                .FirstOrDefaultAsync(b => b.TruckId == vehicle.Id && b.Status != Booking.Domain.Enums.BookingStatus.Canceled && b.Status != Booking.Domain.Enums.BookingStatus.Completed && b.Status != Booking.Domain.Enums.BookingStatus.Expired, cancellationToken);
        }

        // 3. Tìm Driver
        Driver.Domain.Entities.Driver? driver = null;
        if (booking?.DriverId.HasValue == true)
        {
            driver = await _context.Set<Driver.Domain.Entities.Driver>()
                .FirstOrDefaultAsync(d => d.Id == booking.DriverId.Value, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(request.DriverName))
        {
            var name = request.DriverName.Trim().ToUpper();
            driver = await _context.Set<Driver.Domain.Entities.Driver>()
                .FirstOrDefaultAsync(d => d.FullName.ToUpper() == name, cancellationToken);
        }

        // 4. Tìm Container
        Container.Domain.Entities.Container? container = null;
        if (!string.IsNullOrWhiteSpace(request.ContainerNumber))
        {
            var containerNo = request.ContainerNumber.Trim().ToUpper();
            container = await _context.Set<Container.Domain.Entities.Container>()
                .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == containerNo, cancellationToken);

            if (booking == null && container != null)
            {
                var bookingContainer = await _context.Set<BookingContainer>()
                    .FirstOrDefaultAsync(bc => bc.ContainerId == container.Id, cancellationToken);
                if (bookingContainer != null)
                {
                    booking = await _context.Set<Booking.Domain.Entities.Booking>()
                        .FirstOrDefaultAsync(b => b.Id == bookingContainer.BookingId, cancellationToken);
                }
            }
        }

        // 5. Chuẩn bị Context và Đánh giá
        var context = new GateValidationContext
        {
            GateType = string.IsNullOrWhiteSpace(request.GateType) ? "GateIn" : request.GateType,
            GateCode = request.GateCode?.Trim().ToUpper() ?? "GATE-01",
            LaneCode = request.LaneCode?.Trim().ToUpper(),
            VerificationTime = verificationTime,
            VehiclePlate = request.VehiclePlate.Trim().ToUpper(),
            RfidTag = request.RfidTag,
            VehicleDetected = request.VehicleDetected,
            Vehicle = vehicle,
            DriverName = request.DriverName ?? driver?.FullName ?? booking?.DriverName,
            Driver = driver,
            ContainerNumber = request.ContainerNumber,
            Container = container,
            BookingNumber = request.BookingNumber ?? booking?.BookingNumber,
            Booking = booking,
            OperationType = request.OperationType,
            DriverConfirmed = request.DriverConfirmed,
            BillingSettled = request.BillingSettled,
            BillingStatus = request.BillingStatus
        };

        return await _ruleEngine.EvaluateAsync(context, cancellationToken);
    }

    public async Task<GateInApprovalResultDto> ApproveGateInAsync(GateInApprovalRequestDto request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // 1. Tìm Booking tương ứng
        Booking.Domain.Entities.Booking? booking = null;
        if (!string.IsNullOrWhiteSpace(request.BookingNumber))
        {
            var searchNo = request.BookingNumber.Trim().ToUpper();
            booking = await _context.Set<Booking.Domain.Entities.Booking>()
                .FirstOrDefaultAsync(b => b.BookingCode.ToUpper() == searchNo, cancellationToken);
        }

        // 2. Tìm hoặc cập nhật Vehicle
        Vehicle.Domain.Entities.Vehicle? vehicle = null;
        var plate = request.VehiclePlate;
        if (!string.IsNullOrWhiteSpace(plate))
        {
            var normPlate = NormalizePlate(plate);
            var vehicles = await _context.Set<Vehicle.Domain.Entities.Vehicle>().ToListAsync(cancellationToken);
            vehicle = vehicles.FirstOrDefault(v => NormalizePlate(v.PlateNumber) == normPlate);
        }

        if (booking == null && vehicle != null)
        {
            booking = await _context.Set<Booking.Domain.Entities.Booking>()
                .FirstOrDefaultAsync(b => b.TruckId == vehicle.Id && b.Status != BookingStatus.Canceled && b.Status != BookingStatus.Completed, cancellationToken);
        }

        // 3. Tìm Container
        Container.Domain.Entities.Container? container = null;
        var containerNo = request.ContainerNumber;
        if (!string.IsNullOrWhiteSpace(containerNo))
        {
            var searchCont = containerNo.Trim().ToUpper();
            container = await _context.Set<Container.Domain.Entities.Container>()
                .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == searchCont, cancellationToken);
        }

        // 4. Cập nhật các trạng thái nghiệp vụ theo Acceptance Criteria:
        // - Booking = CHECKED-IN
        if (booking != null)
        {
            booking.CheckIn();
        }

        // - Container = IN-YARD
        if (container != null)
        {
            container.Status = "InYard";
            container.UpdatedAt = now;
        }

        // - Vehicle = INSIDE
        if (vehicle != null)
        {
            vehicle.Status = "Inside";
            vehicle.UpdatedAt = now;
        }

        // 5. Tạo hoặc cập nhật Gate-In Record (Lưu Timestamp)
        GateVerificationRecord? record = null;
        if (request.VerificationRecordId.HasValue)
        {
            record = await _verificationRepository.GetByIdAsync(request.VerificationRecordId.Value, cancellationToken);
        }

        if (record == null)
        {
            var verificationCode = $"GVR-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpper()}";
            record = new GateVerificationRecord
            {
                VerificationCode = verificationCode,
                GateCode = request.GateCode.Trim().ToUpper(),
                LaneCode = request.LaneCode?.Trim().ToUpper(),
                VerificationType = "AI_GATE_IN",
                VerificationStatus = "PASS",
                VerificationTime = now,
                DetectedPlate = plate ?? "UNKNOWN",
                VehicleDetected = true,
                BookingId = booking?.Id,
                BookingNumber = booking?.BookingNumber,
                VehicleId = vehicle?.Id,
                VehiclePlate = plate,
                DriverId = booking?.DriverId,
                DriverName = booking?.DriverName,
                Notes = request.Notes ?? "Gate-In approved successfully by Gate Officer.",
                ProcessedBy = request.OfficerId ?? "GATE_OFFICER"
            };
            await _verificationRepository.AddAsync(record, cancellationToken);
        }
        else
        {
            record.VerificationStatus = "PASS";
            record.Notes = request.Notes ?? "Gate-In approved successfully by Gate Officer.";
            record.ProcessedBy = request.OfficerId ?? "GATE_OFFICER";
            record.UpdatedAt = now;
            await _verificationRepository.UpdateAsync(record, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        // 6. Gửi thông báo cho Driver & Dispatcher
        bool driverNotified = false;
        bool dispatcherNotified = false;

        if (_notificationService != null)
        {
            try
            {
                // Thông báo cho Driver
                if (booking?.DriverId.HasValue == true)
                {
                    await _notificationService.SendAsync(new SendNotificationDto
                    {
                        RecipientId = booking.DriverId.Value,
                        Type = NotificationType.GatePassCreated,
                        Severity = NotificationSeverity.Success,
                        Title = "Cổng kiểm soát: Gate-In thành công",
                        Message = $"Phương tiện {plate} đã hoàn tất thủ tục Gate-In tại cổng {request.GateCode}. Vui lòng di chuyển đến vị trí bãi được chỉ định.",
                        ReferenceId = record.VerificationCode
                    }, cancellationToken);
                    driverNotified = true;
                }

                // Thông báo cho Dispatcher
                var dispatcherRecipientId = booking?.ApprovedBy ?? Guid.Empty;
                if (dispatcherRecipientId != Guid.Empty)
                {
                    await _notificationService.SendAsync(new SendNotificationDto
                    {
                        RecipientId = dispatcherRecipientId,
                        Type = NotificationType.SystemAlert,
                        Severity = NotificationSeverity.Info,
                        Title = $"Thông báo Gate-In: {plate}",
                        Message = $"Xe {plate} chở container {container?.ContainerNumber ?? "N/A"} đã vào cổng {request.GateCode} lúc {now:HH:mm:ss}.",
                        ReferenceId = record.VerificationCode
                    }, cancellationToken);
                    dispatcherNotified = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to send gate notification: {ex.Message}");
            }
        }

        return new GateInApprovalResultDto
        {
            Success = true,
            Message = $"Phê duyệt Gate-In thành công cho phương tiện {plate}.",
            Timestamp = now,
            BookingStatus = booking?.Status.ToString() ?? "CheckedIn",
            ContainerStatus = container?.Status ?? "InYard",
            VehicleStatus = vehicle?.Status ?? "Inside",
            GateRecordId = record.Id,
            DriverNotified = driverNotified,
            DispatcherNotified = dispatcherNotified
        };
    }

    public async Task<GateVerificationRecordDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await _verificationRepository.GetByIdAsync(id, cancellationToken);
        return record == null ? null : MapToDto(record);
    }

    public async Task<IReadOnlyList<GateVerificationRecordDto>> GetListAsync(GateVerificationFilterDto filter, CancellationToken cancellationToken = default)
    {
        var records = await _verificationRepository.GetListAsync(filter, cancellationToken);
        return records.Select(MapToDto).ToList();
    }

    public async Task<GateVerificationRecordDto?> ManualOverrideAsync(Guid id, ManualOverrideDto dto, CancellationToken cancellationToken = default)
    {
        var record = await _verificationRepository.GetByIdAsync(id, cancellationToken);
        if (record == null) return null;

        record.VerificationStatus = dto.Approved ? "PASS" : "FAIL";
        record.Notes = $"[Manual Override]: {dto.Reason}";
        record.ProcessedBy = dto.OfficerId ?? "GATE_OFFICER";
        record.UpdatedAt = DateTime.UtcNow;

        await _verificationRepository.UpdateAsync(record, cancellationToken);
        return MapToDto(record);
    }

    private static string NormalizePlate(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return string.Empty;
        return Regex.Replace(plate, @"[^a-zA-Z0-9]", string.Empty).ToUpperInvariant();
    }

    private static GateVerificationRecordDto MapToDto(GateVerificationRecord r)
    {
        return new GateVerificationRecordDto
        {
            Id = r.Id,
            VerificationCode = r.VerificationCode,
            GateCode = r.GateCode,
            LaneCode = r.LaneCode,
            VerificationType = r.VerificationType,
            VerificationStatus = r.VerificationStatus,
            FailureReason = r.FailureReason,
            VerificationTime = r.VerificationTime,
            DetectedPlate = r.DetectedPlate,
            PlateConfidence = r.PlateConfidence,
            RfidTag = r.RfidTag,
            VehicleDetected = r.VehicleDetected,
            CameraId = r.CameraId,
            OcrRawData = r.OcrRawData,
            BookingId = r.BookingId,
            BookingNumber = r.BookingNumber,
            VehicleId = r.VehicleId,
            VehiclePlate = r.VehiclePlate,
            DriverId = r.DriverId,
            DriverName = r.DriverName,
            VehiclePlateImageUrl = r.VehiclePlateImageUrl,
            OverviewImageUrl = r.OverviewImageUrl,
            Notes = r.Notes,
            ProcessedBy = r.ProcessedBy,
            CreatedAt = r.CreatedAt
        };
    }
}

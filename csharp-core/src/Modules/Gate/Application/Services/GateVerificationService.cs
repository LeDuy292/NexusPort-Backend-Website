using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.ExternalServices;
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
    private readonly ILogger<GateVerificationService> _logger;
    private readonly IMessageBrokerService _messageBroker;

    public GateVerificationService(
        IGateVerificationRepository verificationRepository,
        IGateRuleEngine ruleEngine,
        AppDbContext context,
        ILogger<GateVerificationService> logger,
        IMessageBrokerService messageBroker,
        INotificationService? notificationService = null)
    {
        _verificationRepository = verificationRepository;
        _ruleEngine = ruleEngine;
        _context = context;
        _logger = logger;
        _messageBroker = messageBroker;
        _notificationService = notificationService;
    }

    public async Task<GateVerificationResultDto> VerifyGateScanAsync(GateRecognitionEventDto request, CancellationToken cancellationToken = default)
    {
        var verificationTime = DateTime.UtcNow;
        var manual=request.VerificationType?.StartsWith("MANUAL",StringComparison.OrdinalIgnoreCase)==true;
        if(manual && string.IsNullOrWhiteSpace(request.ManualReason))
            throw new NexusPort.Shared.Exceptions.ValidationException("Reason","Nhập lý do xác minh thủ công.");
        var isGateOut = request.VerificationType?.Contains("OUT",StringComparison.OrdinalIgnoreCase)==true;
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
        else
        {
            var entryBookingId=await new GateEntryAccess(_context).ResolveBookingAsync(request.DetectedVehiclePlate,cancellationToken,isGateOut);
            if(entryBookingId.HasValue) booking=await _context.Set<Booking.Domain.Entities.Booking>()
                .FirstOrDefaultAsync(b=>b.Id==entryBookingId.Value,cancellationToken);
        }

        Container.Domain.Entities.Container? container = null;
        if (!string.IsNullOrWhiteSpace(request.ContainerNumber))
        {
            var containerNo = request.ContainerNumber.Trim().ToUpper();
            container = await _context.Set<Container.Domain.Entities.Container>()
                .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == containerNo, cancellationToken);
        }

        var vehicleId = matchedVehicle?.Id ?? booking?.TruckId;
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

        if (container == null && booking != null)
        {
            var bookingContainer = await _context.Set<BookingContainer>()
                .FirstOrDefaultAsync(bc => bc.BookingId == booking.Id, cancellationToken);
            if (bookingContainer != null)
            {
                container = await _context.Set<Container.Domain.Entities.Container>()
                    .FirstOrDefaultAsync(c => c.Id == bookingContainer.ContainerId, cancellationToken);
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
        var entryOrder=booking==null || isGateOut ? null : await new GateEntryAccess(_context).LatestAsync(booking.Id,cancellationToken);

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

            Notes = manual ? $"[Manual: {request.ManualReason}] {message}" : message,
            ProcessedBy = manual ? "GATE_OFFICER" : "AI_YOLO_SYSTEM"
        };

        await _verificationRepository.AddAsync(record, cancellationToken);
        await PublishDispatcherEventAsync(record, gateType, cancellationToken);

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
                BookingNumber = booking.BookingCode,
                Status = booking.Status.ToString(),
                ExpectedVehiclePlate = matchedVehicle?.PlateNumber ?? booking.VehiclePlate ?? request.DetectedVehiclePlate,
                DriverName = driver?.FullName ?? driverName ?? "Nguyễn Văn A",
                DriverLicenseNumber = driver?.LicenseNumber ?? "B2-998877",
                DriverPhone = driver?.Phone ?? "0901 234 567",
                CarrierName = "Công ty CP Vận tải Quốc tế Nexus",
                ContainerNumber = container?.ContainerNumber ?? "MSCU1234567",
                ValidFrom = GateEntryAccess.Utc(entryOrder?.ValidFrom ?? booking.AppointmentStart),
                ValidTo = GateEntryAccess.Utc(entryOrder?.ValidUntil ?? booking.AppointmentEnd),
                GateType = gateType
            },
            Driver = (driver == null && booking == null) ? null : new GateVerificationDriverInfo
            {
                DriverId = driver?.Id ?? driverId ?? Guid.NewGuid(),
                FullName = driver?.FullName ?? driverName ?? "Nguyễn Văn A",
                Phone = driver?.Phone ?? "0901 234 567",
                LicenseNumber = driver?.LicenseNumber ?? "B2-998877",
                IdCardNumber = driver?.IdCardNumber ?? "079090012345",
                Status = driver?.Status.ToString() ?? "active",
                CarrierId = booking?.CarrierId,
                CarrierName = "Công ty CP Vận tải Quốc tế Nexus"
            },
            ImageEvidence = new GateVerificationEvidenceInfo
            {
                VehiclePlateImageUrl = record.VehiclePlateImageUrl,
                OverviewImageUrl = record.OverviewImageUrl
            }
        };
    }

    private async Task PublishDispatcherEventAsync(GateVerificationRecord record, string gateType, CancellationToken cancellationToken)
    {
        try
        {
            var domain = string.Equals(gateType, "GateOut", StringComparison.OrdinalIgnoreCase) ? "gate-out" : "gate";
            await _messageBroker.PublishAsync("dispatcher.status.updated", new DispatcherStatusUpdatedEvent(
                Guid.NewGuid(), domain, record.Id.ToString(), record.VerificationStatus, record.VerificationTime,
                BookingId: record.BookingId, VehicleId: record.VehicleId, DriverId: record.DriverId,
                Label: record.GateCode), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Gate verification saved but dispatcher event could not be published for {RecordId}", record.Id);
        }
    }

    public async Task<GateRuleEvaluationResult> EvaluateRulesAsync(GateRulePreCheckRequestDto request, CancellationToken cancellationToken = default)
    {
        var verificationTime = DateTime.UtcNow;
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
        else
        {
            var bookingId=await new GateEntryAccess(_context).ResolveBookingAsync(request.VehiclePlate,cancellationToken,
                string.Equals(request.GateType,"GateOut",StringComparison.OrdinalIgnoreCase));
            if(bookingId.HasValue) booking=await _context.Set<Booking.Domain.Entities.Booking>().FirstOrDefaultAsync(b=>b.Id==bookingId.Value,cancellationToken);
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
        if(!request.PassageConfirmed || !request.VerificationRecordId.HasValue)
            throw new NexusPort.Shared.Exceptions.ValidationException("Passage","Cần xác nhận xe đã qua cổng và mã xác minh.");
        var evidence=await _verificationRepository.GetByIdAsync(request.VerificationRecordId.Value,cancellationToken);
        if(evidence==null || evidence.VerificationStatus!="PASS" || evidence.GateCode!=request.GateCode.Trim().ToUpperInvariant() ||
          evidence.VerificationType.Contains("OUT",StringComparison.OrdinalIgnoreCase) ||
          NormalizePlate(evidence.DetectedPlate)!=NormalizePlate(request.VehiclePlate))
            throw new NexusPort.Shared.Exceptions.ValidationException("Verification","Xác minh không hợp lệ cho xe và cổng này.");

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

        if (booking == null)
            throw new NexusPort.Shared.Exceptions.ValidationException("Booking", "Cần booking hợp lệ trước khi xác nhận vào cổng.");
        if(evidence.BookingId!=booking.Id)
            throw new NexusPort.Shared.Exceptions.ValidationException("Verification","Xác minh không thuộc booking này.");
        if (container != null && !await _context.Set<Booking.Domain.Entities.BookingContainer>()
            .AnyAsync(x => x.BookingId == booking.Id && x.ContainerId == container.Id && x.ContainerVisitId != null, cancellationToken))
            throw new NexusPort.Shared.Exceptions.ValidationException("Container", "Container không thuộc lượt bãi của booking này.");

        await using var gateTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM bookings WHERE id={booking.Id} FOR UPDATE", cancellationToken);
        await _context.Entry(booking).ReloadAsync(cancellationToken);
        var existingPassage=await _context.Database.SqlQueryRaw<Guid>(
            "SELECT id AS \"Value\" FROM gate_transactions WHERE booking_id={0} AND gate_type='gate_in' AND status='completed'",booking.Id).ToListAsync(cancellationToken);
        if(existingPassage.Count>0)
        {
            await gateTransaction.CommitAsync(cancellationToken);
            return new GateInApprovalResultDto{Success=true,Message="Xe đã được ghi nhận Gate In.",GateRecordId=existingPassage[0],BookingStatus=booking.Status.ToString()};
        }
        if (booking.Status != BookingStatus.Approved && booking.Status != BookingStatus.Ready && booking.Status != BookingStatus.CheckedIn)
            throw new NexusPort.Shared.Exceptions.ValidationException("Booking", "Booking cần được duyệt trước khi vào cổng.");

        var entry=await new GateEntryAccess(_context).LatestAsync(booking.Id,cancellationToken);
        var entryCheck=GateEntryAccess.Check(entry,booking,plate ?? "",now);
        if(!entryCheck.IsSuccess) throw new NexusPort.Shared.Exceptions.ValidationException("EntryOrder",entryCheck.Message);
        var opened=await _context.Database.SqlQueryRaw<int>(@"SELECT count(*)::int AS ""Value"" FROM gate_barrier_commands
            WHERE verification_id={0} AND entry_order_id={1} AND gate_code={2} AND action='open' AND status IN ('simulated','acknowledged')",
            evidence.Id,entry!.Id,request.GateCode.Trim().ToUpperInvariant()).SingleAsync(cancellationToken);
        if(opened==0) throw new NexusPort.Shared.Exceptions.ValidationException("Barrier","Chưa có lệnh mở cổng được chấp nhận cho xác minh này.");

        // 4. Cập nhật các trạng thái nghiệp vụ theo Acceptance Criteria:
        // - Booking = CHECKED-IN
        if (booking != null)
        {
            booking.CheckIn();
        }

        // - Container = IN-YARD
        if (container != null)
        {
            if (booking?.BookingType == Booking.Domain.Enums.BookingType.Dropoff)
                container.Status = "gate_in";
            container.UpdatedAt = now;
        }

        // - Vehicle = INSIDE
        if (vehicle != null)
        {
            vehicle.Status = NexusPort.Modules.Vehicle.Domain.Enums.TruckStatus.active;
            vehicle.UpdatedAt = now;
        }

        // 5. Tạo hoặc cập nhật Gate-In Record (Lưu Timestamp)
        var gateIds = await _context.Database.SqlQueryRaw<Guid>(
            "SELECT id AS \"Value\" FROM gates WHERE code={0} AND is_active", request.GateCode.Trim().ToUpper()).ToListAsync(cancellationToken);
        if (gateIds.Count != 1) throw new NexusPort.Shared.Exceptions.ValidationException("Gate", "Cổng không hợp lệ hoặc đã ngừng hoạt động.");
        var previous = await _context.Database.SqlQueryRaw<Guid>(
            "SELECT id AS \"Value\" FROM gate_transactions WHERE booking_id={0} AND gate_type='gate_in' AND status='completed'", booking.Id).ToListAsync(cancellationToken);
        var gateRecordId = previous.FirstOrDefault();
        if (gateRecordId == Guid.Empty)
        {
            gateRecordId = Guid.NewGuid();
            await _context.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO gate_transactions(id,booking_id,gate_id,driver_id,truck_id,gate_type,status,plate_number_detected,checked_at,completed_at)
              VALUES({gateRecordId},{booking.Id},{gateIds[0]},{booking.DriverId},{booking.TruckId},'gate_in','completed',{plate},{now},{now})", cancellationToken);
        }
        await _context.Database.ExecuteSqlInterpolatedAsync($@"UPDATE gate_entry_orders SET status='consumed',
          consumed_at={now},gate_transaction_id={gateRecordId} WHERE id={entry.Id} AND status='active'",cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await gateTransaction.CommitAsync(cancellationToken);

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
                        ReferenceId = "GATE-" + gateRecordId
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
                        ReferenceId = "GATE-" + gateRecordId
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
            VehicleStatus = vehicle?.Status.ToString() ?? "active",
            GateRecordId = gateRecordId,
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

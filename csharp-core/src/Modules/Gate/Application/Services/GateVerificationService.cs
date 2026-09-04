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

namespace NexusPort.Modules.Gate.Application.Services;

public class GateVerificationService : IGateVerificationService
{
    private readonly IGateVerificationRepository _verificationRepository;
    private readonly IGateRuleEngine _ruleEngine;
    private readonly AppDbContext _context;

    public GateVerificationService(
        IGateVerificationRepository verificationRepository,
        IGateRuleEngine ruleEngine,
        AppDbContext context)
    {
        _verificationRepository = verificationRepository;
        _ruleEngine = ruleEngine;
        _context = context;
    }

    public async Task<GateVerificationResultDto> VerifyGateScanAsync(GateRecognitionEventDto request, CancellationToken cancellationToken = default)
    {
        var verificationTime = request.Timestamp ?? DateTime.UtcNow;
        var normalizedDetectedPlate = NormalizePlate(request.DetectedVehiclePlate);

        // 1. Tìm Booking tương ứng
        Booking.Domain.Entities.Booking? booking = null;

        if (!string.IsNullOrWhiteSpace(request.BookingNumber))
        {
            var searchNumber = request.BookingNumber.Trim().ToUpper();
            booking = await _context.Set<Booking.Domain.Entities.Booking>()
                .FirstOrDefaultAsync(b => b.BookingNumber.ToUpper() == searchNumber, cancellationToken);
        }
        else
        {
            var bookings = await _context.Set<Booking.Domain.Entities.Booking>()
                .Where(b => b.VehiclePlate != null && b.Status != "Cancelled" && b.Status != "Completed" && b.Status != "Expired")
                .ToListAsync(cancellationToken);

            booking = bookings.FirstOrDefault(b => NormalizePlate(b.VehiclePlate) == normalizedDetectedPlate);
        }

        // 2. Tra cứu Vehicle & Driver & Container trong DB nếu có
        var vehicles = await _context.Set<Vehicle.Domain.Entities.Vehicle>().ToListAsync(cancellationToken);
        var matchedVehicle = vehicles.FirstOrDefault(v => NormalizePlate(v.PlateNumber) == normalizedDetectedPlate);
        if (matchedVehicle == null && !string.IsNullOrWhiteSpace(request.RfidTag))
        {
            matchedVehicle = vehicles.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.RfidTag) && 
                string.Equals(v.RfidTag.Trim(), request.RfidTag.Trim(), StringComparison.OrdinalIgnoreCase));
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

        // 3. Khởi tạo GateValidationContext và ủy quyền kiểm tra cho Rule Engine
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
            BookingNumber = request.BookingNumber,
            Booking = booking
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
                Status = booking.Status,
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

        // 1. Tìm Booking
        Booking.Domain.Entities.Booking? booking = null;
        if (!string.IsNullOrWhiteSpace(request.BookingNumber))
        {
            var searchNumber = request.BookingNumber.Trim().ToUpper();
            booking = await _context.Set<Booking.Domain.Entities.Booking>()
                .FirstOrDefaultAsync(b => b.BookingNumber.ToUpper() == searchNumber, cancellationToken);
        }
        else
        {
            var bookings = await _context.Set<Booking.Domain.Entities.Booking>()
                .Where(b => b.VehiclePlate != null && b.Status != "Cancelled" && b.Status != "Completed" && b.Status != "Expired")
                .ToListAsync(cancellationToken);

            booking = bookings.FirstOrDefault(b => NormalizePlate(b.VehiclePlate) == normalizedPlate);
        }

        // 2. Tìm Vehicle
        var vehicles = await _context.Set<Vehicle.Domain.Entities.Vehicle>().ToListAsync(cancellationToken);
        var vehicle = vehicles.FirstOrDefault(v => NormalizePlate(v.PlateNumber) == normalizedPlate);

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
            Booking = booking
        };

        return await _ruleEngine.EvaluateAsync(context, cancellationToken);
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

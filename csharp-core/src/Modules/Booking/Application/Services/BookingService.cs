using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Notifications.DTOs;
using NexusPort.Infrastructure.Notifications.Enums;
using NexusPort.Infrastructure.Notifications.Interfaces;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Modules.Booking.Domain.Entities;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Shared.Exceptions;
using NexusPort.Shared.Results;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Container.Domain.Entities;
using NexusPort.Modules.Driver.Domain.Enums;
using Microsoft.EntityFrameworkCore.Storage;

namespace NexusPort.Modules.Booking.Application.Services;

public partial class BookingService : IBookingService
{
    private readonly NexusPort.Infrastructure.ExternalServices.IS3StorageService _fleetStorage;
    private readonly IBookingRepository _repository;
    private readonly IBookingValidationService _validationService;
    private readonly INotificationService _notificationService;
    private readonly AppDbContext _context;
    private readonly ILogger<BookingService> _logger;
    private readonly IBookingCompatibilityPolicy _compatibilityPolicy;
    private readonly IBookingDocumentRequirementPolicy _documentRequirementPolicy;

    public BookingService(
        IBookingRepository repository,
        IBookingValidationService validationService,
        INotificationService notificationService,
        AppDbContext context,
        ILogger<BookingService> logger,
        IBookingCompatibilityPolicy compatibilityPolicy,
        IBookingDocumentRequirementPolicy documentRequirementPolicy,
        NexusPort.Infrastructure.ExternalServices.IS3StorageService fleetStorage)
    {
        _fleetStorage = fleetStorage;
        _repository = repository;
        _validationService = validationService;
        _notificationService = notificationService;
        _context = context;
        _logger = logger;
        _compatibilityPolicy = compatibilityPolicy;
        _documentRequirementPolicy = documentRequirementPolicy;
    }

    public async Task<IReadOnlyList<BookingDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        var dtos=entities.Select(MapToDto).ToList();
        foreach(var dto in dtos) SignFleetDocuments(dto.VehicleDetails,dto.DriverDetails);
        return dtos;
    }

    public async Task<PagedResult<BookingDto>> GetPagedAsync(BookingFilterParams filter, CancellationToken cancellationToken = default)
    {
        var pagedEntities = await _repository.GetPagedAsync(filter, cancellationToken);
        var dtos = pagedEntities.Items.Select(MapToDto).ToList();
        await EnrichBookingDtosAsync(dtos, cancellationToken);

        return new PagedResult<BookingDto>(dtos, pagedEntities.TotalCount, pagedEntities.PageNumber, pagedEntities.PageSize);
    }

    public async Task<BookingDto?> GetByIdAsync(Guid id, Guid? userCarrierId = null, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdWithContainersAsync(id, cancellationToken);
        if (entity == null) return null;

        // Tenant Isolation Check
        if (userCarrierId.HasValue && userCarrierId.Value != Guid.Empty && entity.CarrierId != userCarrierId.Value)
        {
            throw new UnauthorizedException("Access denied. You do not have permission to view this Booking.");
        }

        var dto = MapToDto(entity);
        await EnrichBookingDtosAsync(new List<BookingDto> { dto }, cancellationToken);
        return dto;
    }

    public async Task<BookingDto> CreateAsync(CreateBookingDto dto, CancellationToken cancellationToken = default)
    {
        BookingFleetPolicy.ValidateWindow(dto.AppointmentStart, dto.AppointmentEnd);
        await using var bookingTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await LockBookingFleetAsync(dto.TruckId, dto.DriverId, cancellationToken);
        await PrepareBookingCargoAsync(dto, cancellationToken);
        await EnsureBookingFleetAsync(dto.CarrierId, dto.TruckId, dto.DriverId, dto.AppointmentStart,
            dto.AppointmentEnd, null, cancellationToken);
        bool hasMultiOperations = dto.Operations.Count > 0;

        // Execute Business Validation Rules
        await _validationService.ValidateBookingAsync(dto, cancellationToken);

        var entity = new Domain.Entities.Booking(
            dto.CarrierId,
            dto.BookingCode,
            dto.BookingType,
            dto.AppointmentStart,
            dto.AppointmentEnd,
            dto.DriverId,
            dto.TruckId
        );

        entity.IsCombined = dto.IsCombined || (hasMultiOperations && dto.Operations.Count > 1);
        entity.CustomerId = dto.CustomerId;
        entity.TripStatus = "Scheduled";
        entity.VehiclePlate = dto.VehiclePlate;
        entity.VehicleId = dto.VehicleId;
        entity.DriverName = dto.DriverName;
        entity.ValidFrom = dto.ValidFrom ?? dto.AppointmentStart;
        entity.ValidTo = dto.ValidTo ?? dto.AppointmentEnd;
        entity.GateType = dto.GateType ?? "GateIn";
        entity.Description = dto.Description;

        entity.TrailerNumber = string.IsNullOrWhiteSpace(dto.TrailerNumber) ? null : dto.TrailerNumber.Trim().ToUpperInvariant();
        await CaptureFleetDetailsAsync(entity,cancellationToken);

        // Auto populate DriverName if empty
        if (string.IsNullOrWhiteSpace(entity.DriverName) && dto.DriverId.HasValue && dto.DriverId.Value != Guid.Empty)
        {
            var driver = await _context.Set<NexusPort.Modules.Driver.Domain.Entities.Driver>()
                .AsNoTracking().FirstOrDefaultAsync(d => d.Id == dto.DriverId.Value, cancellationToken);
            if (driver != null) entity.DriverName = driver.FullName;
        }

        // Auto populate VehiclePlate if empty
        if (string.IsNullOrWhiteSpace(entity.VehiclePlate) && dto.TruckId.HasValue && dto.TruckId.Value != Guid.Empty)
        {
            var truck = await _context.Set<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>()
                .AsNoTracking().FirstOrDefaultAsync(v => v.Id == dto.TruckId.Value, cancellationToken);
            if (truck != null) entity.VehiclePlate = truck.PlateNumber;
        }

        if (hasMultiOperations)
        {
            foreach (var opDto in dto.Operations.OrderBy(x => x.SequenceNumber))
            {
                var op = new Domain.Entities.BookingOperation(
                    entity.Id,
                    opDto.OperationType,
                    opDto.ContainerNumber,
                    opDto.ExpectedGateDirection,
                    opDto.CargoStatus ?? "Loaded",
                    opDto.SequenceNumber);

                op.ContainerIsoSize = string.IsNullOrWhiteSpace(opDto.ContainerIsoSize) ? "20GP" : opDto.ContainerIsoSize;
                op.CargoType = string.IsNullOrWhiteSpace(opDto.CargoType) ? "General" : opDto.CargoType;
                op.CarrierBookingNumber = opDto.CarrierBookingNumber;
                op.BillOfLadingNumber = opDto.BillOfLadingNumber;
                op.DeliveryOrderNumber = opDto.DeliveryOrderNumber;
                op.CustomsDeclarationNumber = opDto.CustomsDeclarationNumber;
                op.SealNumber = opDto.SealNumber;
                op.GrossWeightKg = opDto.GrossWeightKg;
                op.TemperatureSetpoint = opDto.TemperatureSetpoint;
                op.TemperatureUnit = opDto.TemperatureUnit ?? "C";
                op.UnNumber = opDto.UnNumber;
                op.DgClass = opDto.DgClass;
                op.SpecialHandlingRequirements = opDto.SpecialHandlingRequirements;

                // Lookup container ID in DB if available
                Guid? contId = opDto.ContainerId;
                if (!contId.HasValue || contId.Value == Guid.Empty)
                {
                    var cleanNo = opDto.ContainerNumber.Trim().ToUpperInvariant();
                    var existingCont = await _context.Set<Container.Domain.Entities.Container>()
                        .FirstOrDefaultAsync(x => x.ContainerNumber.ToUpper() == cleanNo, cancellationToken);
                    if (existingCont != null) contId = existingCont.Id;
                }

                if (contId.HasValue && contId.Value != Guid.Empty)
                {
                    op.ContainerId = contId.Value;
                    entity.AddContainer(contId.Value);
                    var selection = dto.ContainerSelections.Single(s => s.ContainerId == contId.Value);
                    if (dto.BookingType == BookingType.Pickup)
                    {
                        var prepared = await _context.Database.SqlQueryRaw<PickupCodeLink>(
                            "SELECT * FROM yard_prepare_pickup({0},{1},{2})", selection.ContainerId,
                            NormalizeYardCode(selection.YardCode), dto.CarrierId).ToListAsync(cancellationToken);
                        selection.DeclarationId = prepared.Single().DeclarationId;
                        selection.ContainerVisitId = prepared.Single().ContainerVisitId;
                    }
                    var link = entity.BookingContainers.Single(c => c.ContainerId == contId.Value);
                    link.DeclarationId = selection.DeclarationId;
                    link.ContainerVisitId = selection.ContainerVisitId;
                }

                if (opDto.Documents != null)
                {
                    foreach (var docDto in opDto.Documents)
                    {
                        var doc = new Domain.Entities.BookingDocument(
                            entity.Id,
                            docDto.DocumentType,
                            docDto.DocumentNumber,
                            docDto.FileUrl,
                            docDto.FileName,
                            op.Id);
                        doc.Issuer = docDto.Issuer;
                        doc.FileSizeBytes = docDto.FileSizeBytes;
                        doc.ExpiryDate = docDto.ExpiryDate;
                        op.Documents.Add(doc);
                    }
                }

                entity.AddOperation(op);
            }

            if (dto.Documents != null)
            {
                foreach (var docDto in dto.Documents)
                {
                    var doc = new Domain.Entities.BookingDocument(
                        entity.Id,
                        docDto.DocumentType,
                        docDto.DocumentNumber,
                        docDto.FileUrl,
                        docDto.FileName,
                        docDto.BookingOperationId);
                    doc.Issuer = docDto.Issuer;
                    doc.FileSizeBytes = docDto.FileSizeBytes;
                    doc.ExpiryDate = docDto.ExpiryDate;
                    entity.AddDocument(doc);
                }
            }
        }
        else
        {
            if (dto.ContainerSelections.Count == 0 ||
                dto.ContainerSelections.Select(x => x.ContainerId).Distinct().Count() != dto.ContainerSelections.Count)
                throw new ValidationException("ContainerSelections", "Chọn container từ trang khai báo trước khi đặt lịch.");

            foreach (var selection in dto.ContainerSelections.OrderBy(x => x.ContainerId))
            {
                if (dto.BookingType == BookingType.Pickup)
                {
                    if (string.IsNullOrWhiteSpace(selection.YardCode) ||
                        !System.Text.RegularExpressions.Regex.IsMatch(NormalizeYardCode(selection.YardCode), @"^TC-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}$"))
                        throw new ValidationException("YardCode", "Nhập mã lượt bãi nhận được từ booking Dropoff.");
                    var links = await _context.Database.SqlQueryRaw<PickupCodeLink>(
                        "SELECT * FROM yard_prepare_pickup({0},{1},{2})", selection.ContainerId, NormalizeYardCode(selection.YardCode), dto.CarrierId)
                        .ToListAsync(cancellationToken);
                    selection.DeclarationId = links.Single().DeclarationId;
                    selection.ContainerVisitId = links.Single().ContainerVisitId;
                }
                else if (selection.DeclarationId == Guid.Empty || selection.ContainerVisitId.HasValue)
                    throw new ValidationException("ContainerSelections", "Chọn khai báo chưa đặt Dropoff từ trang khai báo container.");
            }
            foreach (var selection in dto.ContainerSelections.OrderBy(x => x.ContainerId))
            {
                entity.AddContainer(selection.ContainerId);
                var link = entity.BookingContainers.Last();
                link.DeclarationId = selection.DeclarationId;
                link.ContainerVisitId = selection.ContainerVisitId;

                var opType = dto.BookingType == BookingType.Dropoff ? "Export" : "Import";
                var gateDir = dto.BookingType == BookingType.Dropoff ? "GateIn" : "GateOut";
                var op = new Domain.Entities.BookingOperation(
                    entity.Id,
                    opType,
                    selection.ContainerNumber ?? "CONT-UNSPECIFIED",
                    gateDir,
                    "Loaded",
                    1)
                {
                    ContainerId = selection.ContainerId,
                    CargoType = selection.CargoTypeName ?? "General"
                };
                entity.AddOperation(op);
            }
        }

        // A future appointment requests power, but does not occupy a physical socket.
        if (dto.IsReeferPowerRequired && dto.BookingType == BookingType.Dropoff)
        {
            entity.IsReeferPowerRequired = true;
            entity.TargetTemperature = dto.TargetTemperature;
            entity.TemperatureUnit = dto.TemperatureUnit ?? "C";
            entity.TempTolerance = dto.TempTolerance;
            entity.RequiredPowerKw = dto.RequiredPowerKw;
            entity.ReeferPowerStatus = "PendingAssignment";
        }

        // Theo yêu cầu: Luôn đặt trạng thái là Pending để chờ Dispatcher duyệt
        entity.Status = BookingStatus.Pending;

        await _repository.AddAsync(entity, cancellationToken);
        if (entity.IsReeferPowerRequired)
        {
            var requestId = Guid.NewGuid();
            var requestNo = "REQ-RFR-" + requestId.ToString("N").ToUpperInvariant();
            var containerId = dto.ContainerSelections.Single().ContainerId;
            await _context.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO public.reefer_power_requests
                  (id,request_no,container_id,booking_id,booking_code,target_temperature,temperature_unit,
                   temp_tolerance,required_power_kw,cargo_description,special_instructions,status,created_at,updated_at)
                VALUES ({requestId},{requestNo},{containerId},{entity.Id},{entity.BookingCode},{entity.TargetTemperature},
                  {entity.TemperatureUnit},{entity.TempTolerance},{entity.RequiredPowerKw},{entity.Description},
                  {dto.SpecialColdInstructions},'PENDING_ASSIGNMENT'::reefer_request_status,NOW(),NOW())", cancellationToken);
        }
        await bookingTransaction.CommitAsync(cancellationToken);

        // Emit real business notification to Database (NXP-044)
        try
        {
            Guid recipientUserId = Guid.Empty;
            try
            {
                var conn = _context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(cancellationToken);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT user_id FROM carrier_users WHERE carrier_id = @p0 LIMIT 1";
                var p = cmd.CreateParameter();
                p.ParameterName = "@p0";
                p.Value = entity.CarrierId;
                cmd.Parameters.Add(p);
                var resolved = await cmd.ExecuteScalarAsync(cancellationToken);
                if (resolved != null && resolved != DBNull.Value && Guid.TryParse(resolved.ToString(), out var uId))
                {
                    recipientUserId = uId;
                }
            }
            catch { }

            if (recipientUserId != Guid.Empty)
            {
                await _notificationService.SendAsync(new SendNotificationDto
                {
                    RecipientId = recipientUserId,
                    Title = $"Tạo mới Booking {entity.BookingCode}",
                    Message = $"Lịch hẹn {entity.BookingType} mã {entity.BookingCode} đã khởi tạo thành công lúc {DateTime.UtcNow:HH:mm dd/MM/yyyy}. Trạng thái: {entity.Status}.",
                    Type = NotificationType.BookingApproved,
                    Severity = NotificationSeverity.Success,
                    ReferenceId = entity.BookingCode
                }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for booking {BookingCode}", entity.BookingCode);
        }

        var createdDto = MapToDto(entity);
        await EnrichBookingDtosAsync(new List<BookingDto> { createdDto }, cancellationToken);
        return createdDto;
    }

    public async Task<BookingDto> UpdateAsync(Guid id, UpdateBookingDto dto, Guid? userCarrierId = null, CancellationToken cancellationToken = default)
    {
        await using var fleetTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM bookings WHERE id={0} FOR UPDATE", id).ToListAsync(cancellationToken);
        var entity = await _repository.GetByIdWithContainersAsync(id, cancellationToken);
        if (entity == null)
        {
            throw new NotFoundException("Booking", id);
        }

        // Tenant Isolation Check
        if (userCarrierId.HasValue && userCarrierId.Value != Guid.Empty && entity.CarrierId != userCarrierId.Value)
        {
            throw new UnauthorizedException("Access denied. You can only update Bookings belonging to your company.");
        }

        // Business Rule Check: Only Pending bookings can be updated by Transport Company
        if (entity.Status != BookingStatus.Pending)
        {
            throw new ValidationException("Status", $"Lịch hẹn ở trạng thái '{entity.Status}' không thể cập nhật.");
        }

        // Re-validate updated request values
        var validationDto = new CreateBookingDto
        {
            CarrierId = entity.CarrierId,
            BookingCode = entity.BookingCode,
            BookingType = entity.BookingType,
            DriverId = dto.DriverId ?? entity.DriverId,
            TruckId = dto.TruckId ?? entity.TruckId,
            AppointmentStart = dto.AppointmentStart,
            AppointmentEnd = dto.AppointmentEnd,
            ContainerIds = entity.BookingContainers.Select(x => x.ContainerId).ToList(),
            ExcludeBookingId = entity.Id
        };

        await LockBookingFleetAsync(validationDto.TruckId, validationDto.DriverId, cancellationToken);
        await EnsureBookingFleetAsync(entity.CarrierId, validationDto.TruckId, validationDto.DriverId,
            dto.AppointmentStart, dto.AppointmentEnd, entity.Id, cancellationToken);
        await _validationService.ValidateBookingAsync(validationDto, cancellationToken);

        // Update properties
        entity.DriverId = dto.DriverId ?? entity.DriverId;
        entity.TruckId = dto.TruckId ?? entity.TruckId;
        entity.AppointmentStart = dto.AppointmentStart;
        entity.AppointmentEnd = dto.AppointmentEnd;
        if (dto.TrailerNumber != null) entity.TrailerNumber=dto.TrailerNumber.Trim().ToUpperInvariant();
        await CaptureFleetDetailsAsync(entity,cancellationToken);

        if (dto.ContainerIds.Count > 0 && !dto.ContainerIds.OrderBy(x=>x).SequenceEqual(entity.BookingContainers.Select(x=>x.ContainerId).OrderBy(x=>x)))
            throw new ValidationException("ContainerIds", "Không thay container/bản khai trên booking đã lưu; hãy hủy và tạo lịch mới.");

        await _repository.UpdateAsync(entity, cancellationToken);
        await fleetTransaction.CommitAsync(cancellationToken);

        // Emit real business notification to Database (NXP-044)
        await _notificationService.SendAsync(new SendNotificationDto
        {
            RecipientId = entity.CarrierId,
            Title = $"Cập nhật Booking {entity.BookingCode}",
            Message = $"Lịch hẹn {entity.BookingCode} đã được điều chỉnh khung giờ sang {entity.AppointmentStart:HH:mm dd/MM/yyyy}.",
            Type = NotificationType.BookingApproved,
            Severity = NotificationSeverity.Info,
            ReferenceId = entity.BookingCode
        }, cancellationToken);

        var updateDto = MapToDto(entity);
        await EnrichBookingDtosAsync(new List<BookingDto> { updateDto }, cancellationToken);
        return updateDto;
    }

    public async Task<BookingDto> CancelAsync(Guid id, CancelBookingDto dto, Guid? userCarrierId = null, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdWithContainersAsync(id, cancellationToken);
        if (entity == null)
        {
            throw new NotFoundException("Booking", id);
        }

        // Tenant Isolation Check
        if (userCarrierId.HasValue && userCarrierId.Value != Guid.Empty && entity.CarrierId != userCarrierId.Value)
        {
            throw new UnauthorizedException("Access denied. You can only cancel Bookings belonging to your company.");
        }

        // Business Rule Check: Cannot cancel if already checked-in or completed
        if (entity.Status == BookingStatus.CheckedIn || entity.Status == BookingStatus.Completed)
        {
            throw new ValidationException("Status", $"Lịch hẹn ở trạng thái '{entity.Status}' không thể hủy.");
        }

        if (entity.Status == BookingStatus.Canceled)
        {
            throw new ValidationException("Status", "Lịch hẹn đã bị hủy trước đó.");
        }

        entity.Cancel();
        if (!string.IsNullOrWhiteSpace(dto?.Reason))
        {
            entity.RejectedReason = dto.Reason;
        }

        if (entity.DriverId.HasValue)
        {
            var drv = await _context.Set<NexusPort.Modules.Driver.Domain.Entities.Driver>()
                .FirstOrDefaultAsync(d => d.Id == entity.DriverId.Value, cancellationToken);
            if (drv != null)
            {
                if (drv.Status == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.waiting_confirmation)
                {
                    drv.Status = NexusPort.Modules.Driver.Domain.Enums.DriverStatus.active;
                    
                    var vehicles = await _context.Set<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>()
                        .Where(v => v.DriverId == drv.Id)
                        .ToListAsync(cancellationToken);
                    foreach (var v in vehicles)
                    {
                        v.DriverId = null;
                    }
                }
                else if (drv.Status == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.booking_confirmed)
                {
                    drv.Status = NexusPort.Modules.Driver.Domain.Enums.DriverStatus.vehicle_received;
                }
            }
        }

        await _repository.UpdateAsync(entity, cancellationToken);

        // Emit real business notification to Database (NXP-044)
        await _notificationService.SendAsync(new SendNotificationDto
        {
            RecipientId = entity.CarrierId,
            Title = $"Hủy Booking {entity.BookingCode}",
            Message = $"Booking {entity.BookingCode} đã bị hủy bỏ. Lý do: {entity.RejectedReason ?? "Theo yêu cầu của người dùng"}.",
            Type = NotificationType.BookingRejected,
            Severity = NotificationSeverity.Warning,
            ReferenceId = entity.BookingCode
        }, cancellationToken);

        var cancelDto = MapToDto(entity);
        await EnrichBookingDtosAsync(new List<BookingDto> { cancelDto }, cancellationToken);
        return cancelDto;
    }

    public async Task<BookingDto> ApproveAsync(Guid id, Guid approvedBy, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdWithContainersAsync(id, cancellationToken);
        if (entity == null)
        {
            throw new NotFoundException("Booking", id);
        }

        if (entity.Status != BookingStatus.Pending && entity.Status != BookingStatus.Ready)
        {
            if (entity.Status == BookingStatus.Approved)
            {
                var existingDto = MapToDto(entity);
                await EnrichBookingDtosAsync(new List<BookingDto> { existingDto }, cancellationToken);
                return existingDto;
            }
            throw new ValidationException("Status", $"Lịch hẹn ở trạng thái '{entity.Status}' không thể duyệt.");
        }

        entity.Approve(approvedBy);

        await _repository.UpdateAsync(entity, cancellationToken);

        // Notify Carrier
        await _notificationService.SendAsync(new SendNotificationDto
        {
            RecipientId = entity.CarrierId,
            Title = $"Gate Booking {entity.BookingCode} đã được duyệt",
            Message = $"Booking {entity.BookingCode} của bạn đã được Dispatcher phê duyệt thành công.",
            Type = NotificationType.BookingApproved,
            Severity = NotificationSeverity.Success,
            ReferenceId = entity.BookingCode
        }, cancellationToken);

        var dto = MapToDto(entity);
        await EnrichBookingDtosAsync(new List<BookingDto> { dto }, cancellationToken);
        return dto;
    }

    public async Task<BookingDto> RejectAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdWithContainersAsync(id, cancellationToken);
        if (entity == null)
        {
            throw new NotFoundException("Booking", id);
        }

        if (entity.Status != BookingStatus.Pending && entity.Status != BookingStatus.Ready)
        {
            if (entity.Status == BookingStatus.Rejected)
            {
                var existingDto = MapToDto(entity);
                await EnrichBookingDtosAsync(new List<BookingDto> { existingDto }, cancellationToken);
                return existingDto;
            }
            throw new ValidationException("Status", $"Lịch hẹn ở trạng thái '{entity.Status}' không thể từ chối.");
        }

        entity.Reject(reason);

        // Hoàn trả trạng thái Container về 'in_yard' khi Booking bị từ chối
        if (entity.DriverId.HasValue)
        {
            var drv = await _context.Set<NexusPort.Modules.Driver.Domain.Entities.Driver>()
                .FirstOrDefaultAsync(d => d.Id == entity.DriverId.Value, cancellationToken);
            if (drv != null)
            {
                if (drv.Status == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.waiting_confirmation || 
                    drv.Status == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.receiving_vehicle)
                {
                    drv.Status = NexusPort.Modules.Driver.Domain.Enums.DriverStatus.active;
                    
                    var vehicles = await _context.Set<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>()
                        .Where(v => v.DriverId == drv.Id)
                        .ToListAsync(cancellationToken);
                    foreach (var v in vehicles)
                    {
                        v.DriverId = null;
                    }
                }
                else if (drv.Status == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.booking_confirmed)
                {
                    drv.Status = NexusPort.Modules.Driver.Domain.Enums.DriverStatus.vehicle_received;
                }
            }
        }

        await _repository.UpdateAsync(entity, cancellationToken);

        // Notify Carrier
        await _notificationService.SendAsync(new SendNotificationDto
        {
            RecipientId = entity.CarrierId,
            Title = $"Gate Booking {entity.BookingCode} đã bị từ chối",
            Message = $"Booking {entity.BookingCode} của bạn đã bị Dispatcher từ chối. Lý do: {reason}",
            Type = NotificationType.BookingRejected,
            Severity = NotificationSeverity.Critical,
            ReferenceId = entity.BookingCode
        }, cancellationToken);

        var dto = MapToDto(entity);
        await EnrichBookingDtosAsync(new List<BookingDto> { dto }, cancellationToken);
        return dto;
    }

    public async Task<IReadOnlyList<DriverContainerOperationDto>> GetDriverOperationsAsync(Guid driverId, CancellationToken cancellationToken = default)
    {
        var bookings = await _context.Set<Domain.Entities.Booking>()
            .AsNoTracking()
            .Include(x => x.BookingContainers)
            .Where(x => x.DriverId == driverId && x.Status == BookingStatus.CheckedIn)
            .OrderBy(x => x.AppointmentStart)
            .ToListAsync(cancellationToken);

        if (bookings.Count == 0) return Array.Empty<DriverContainerOperationDto>();
        var completedOperations = await _context.Set<NexusPort.Modules.Yard.Domain.Entities.YardOperationEvent>()
            .AsNoTracking().Where(x => x.DriverId == driverId && x.OperationStatus == "Completed")
            .Select(x => new { x.ContainerId, x.OccurredAt }).ToListAsync(cancellationToken);
        var entryTimes = await _context.Database.SqlQueryRaw<BookingGateEntryTime>(
            "SELECT booking_id AS \"BookingId\",max(checked_at) AS \"CheckedAt\" FROM gate_transactions WHERE booking_id=ANY({0}) AND gate_type='gate_in' AND status='completed' GROUP BY booking_id",
            bookings.Select(x => x.Id).ToArray()).ToDictionaryAsync(x=>x.BookingId,x=>x.CheckedAt,cancellationToken);
        var containerIds = bookings.SelectMany(x => x.BookingContainers.Select(bc => bc.ContainerId)).Distinct().ToList();
        var containers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .AsNoTracking()
            .Where(x => containerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return bookings.SelectMany(booking => booking.BookingContainers
            .Where(link => containers.ContainsKey(link.ContainerId) && entryTimes.TryGetValue(booking.Id,out var enteredAt) && completedOperations.Any(op=>op.ContainerId==link.ContainerId && op.OccurredAt>=enteredAt))
            .Select(link =>
            {
                var container = containers[link.ContainerId];
                return new DriverContainerOperationDto
                {
                    BookingId = booking.Id,
                    BookingCode = booking.BookingCode,
                    BookingType = booking.BookingType,
                    BookingStatus = booking.Status,
                    ContainerId = container.Id,
                    ContainerNumber = container.ContainerNumber,
                    ContainerStatus = container.Status,
                    OperationStatus = "Completed"
                };
            })).ToList();
    }

    public async Task<ContainerConfirmationResultDto> ConfirmContainerAsync(ContainerConfirmationDto dto, Guid driverId, CancellationToken cancellationToken = default)
    {
        if (driverId == Guid.Empty)
            throw new UnauthorizedException("A valid authenticated driver is required.");

        var driverExists = await _context.Set<NexusPort.Modules.Driver.Domain.Entities.Driver>()
            .AnyAsync(x => x.Id == driverId && x.Status != DriverStatus.inactive && x.Status != DriverStatus.banned, cancellationToken);
        if (!driverExists)
            throw new UnauthorizedException("Only an active assigned driver can confirm a container.");

        if (dto.BookingId == Guid.Empty) throw new ValidationException("BookingId", "Cần xác định đúng booking để xác nhận giao nhận.");
        var booking = await _context.Set<Domain.Entities.Booking>()
            .Include(x => x.BookingContainers)
            .Where(x => x.Id == dto.BookingId && x.DriverId == driverId)
            .Where(x => x.BookingContainers.Any(link => link.ContainerId == dto.ContainerId))
            .Where(x => x.Status != BookingStatus.Canceled)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (booking == null)
            throw new UnauthorizedException("The container is not assigned to the authenticated driver.");

        var gateTimes = await _context.Database.SqlQueryRaw<DateTime>(
            "SELECT checked_at AS \"Value\" FROM gate_transactions WHERE booking_id={0} AND gate_type='gate_in' AND status='completed' ORDER BY checked_at DESC LIMIT 1",booking.Id).ToListAsync(cancellationToken);
        if (gateTimes.Count == 0) throw new ValidationException("Booking", "Booking chưa vào cổng.");
        var enteredAt = gateTimes[0];
        var operationCompleted = await _context.Set<NexusPort.Modules.Yard.Domain.Entities.YardOperationEvent>()
            .AnyAsync(x => x.DriverId == driverId && x.ContainerId == dto.ContainerId && x.OperationStatus == "Completed" && x.OccurredAt >= enteredAt, cancellationToken);
        if (!operationCompleted) throw new ValidationException("Container", "Chưa hoàn tất thao tác bãi của booking này.");

        var container = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>().FirstOrDefaultAsync(x => x.Id == dto.ContainerId, cancellationToken);
        if (container == null)
            throw new NotFoundException("Container", dto.ContainerId);

        var existingAudit = await _context.Set<ContainerConfirmationAudit>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BookingId == booking.Id && x.ContainerId == container.Id, cancellationToken);
        if (existingAudit != null)
        {
            return new ContainerConfirmationResultDto
            {
                ConfirmationId = existingAudit.Id,
                BookingId = booking.Id,
                ContainerId = container.Id,
                ContainerNumber = container.ContainerNumber,
                ContainerStatus = existingAudit.ContainerStatusAfter,
                BookingStatus = booking.Status,
                DriverId = driverId,
                ConfirmedAt = existingAudit.ConfirmedAt,
                Condition = existingAudit.Condition
            };
        }

        if (booking.Status != BookingStatus.CheckedIn) throw new ValidationException("Booking", "Booking cần đang thực hiện giao nhận.");

        var normalizedCondition = string.IsNullOrWhiteSpace(dto.Condition) ? "OK" : dto.Condition.Trim().ToUpperInvariant();
        if (normalizedCondition is not ("OK" or "DAMAGED" or "ISSUE"))
            throw new ValidationException("Condition", "Condition must be OK, DAMAGED, or ISSUE.");

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var readyForGateOut = booking.BookingType == BookingType.Dropoff ? "in_yard" : "moving";
            container.Status = readyForGateOut;
            booking.Complete();

            var audit = new ContainerConfirmationAudit
            {
                BookingId = booking.Id,
                ContainerId = container.Id,
                DriverId = driverId,
                Condition = normalizedCondition,
                Notes = dto.Notes,
                ContainerStatusAfter = readyForGateOut,
                ConfirmedAt = DateTime.UtcNow
            };
            await _context.Set<ContainerConfirmationAudit>().AddAsync(audit, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            try
            {
                await _notificationService.SendAsync(new SendNotificationDto
                {
                    RecipientId = driverId,
                    Type = NotificationType.ContainerReady,
                    Severity = normalizedCondition == "OK" ? NotificationSeverity.Success : NotificationSeverity.Warning,
                    Title = $"Container {container.ContainerNumber} sẵn sàng Gate-Out",
                    Message = $"Container {container.ContainerNumber} đã được xác nhận ({normalizedCondition}). Operation status: ReadyForGateOut.",
                    ReferenceId = container.Id.ToString()
                }, cancellationToken);
            }
            catch (Exception notificationError)
            {
                _logger.LogWarning(notificationError, "Container confirmation succeeded but driver notification failed for container {ContainerId}", container.Id);
            }

            return new ContainerConfirmationResultDto
            {
                ConfirmationId = audit.Id,
                BookingId = booking.Id,
                ContainerId = container.Id,
                ContainerNumber = container.ContainerNumber,
                ContainerStatus = readyForGateOut,
                BookingStatus = booking.Status,
                DriverId = driverId,
                ConfirmedAt = audit.ConfirmedAt,
                Condition = normalizedCondition
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<BookingDto> AssignResourcesAsync(Guid id, AssignBookingResourcesDto dto, Guid? userCarrierId = null, CancellationToken cancellationToken = default)
    {
        await using var fleetTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM bookings WHERE id={0} FOR UPDATE", id).ToListAsync(cancellationToken);
        var entity = await _repository.GetByIdWithContainersAsync(id, cancellationToken);
        if (entity == null)
        {
            throw new NotFoundException("Booking", id);
        }

        if (userCarrierId.HasValue && userCarrierId.Value != Guid.Empty && entity.CarrierId != userCarrierId.Value)
        {
            throw new UnauthorizedException("Access denied. You can only assign resources to Bookings belonging to your company.");
        }

        if (entity.Status is not (BookingStatus.Pending or BookingStatus.Ready or BookingStatus.Approved))
        {
            throw new ValidationException("Status", $"Lịch hẹn ở trạng thái '{entity.Status}' không thể điều phối lại phương tiện.");
        }

        // Validate Driver
        var driver = await _context.Set<NexusPort.Modules.Driver.Domain.Entities.Driver>()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == dto.DriverId, cancellationToken);
        if (driver == null)
        {
            throw new ValidationException("DriverId", $"Không tìm thấy thông tin tài xế trong hệ thống.");
        }
        if (driver.Status == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.inactive || driver.Status == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.banned)
        {
            throw new ValidationException("DriverId", $"Tài xế '{driver.FullName}' hiện không ở trạng thái sẵn sàng hoạt động.");
        }

        // Validate Vehicle / Truck
        var truck = await _context.Set<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>()
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == dto.TruckId, cancellationToken);
        if (truck == null)
        {
            throw new ValidationException("TruckId", $"Không tìm thấy thông tin xe đầu kéo trong hệ thống.");
        }
        if (truck.Status == NexusPort.Modules.Vehicle.Domain.Enums.TruckStatus.inactive || truck.Status == NexusPort.Modules.Vehicle.Domain.Enums.TruckStatus.maintenance)
        {
            throw new ValidationException("TruckId", $"Xe đầu kéo '{truck.PlateNumber}' hiện không ở trạng thái sẵn sàng hoạt động.");
        }

        if (driver.CarrierId != entity.CarrierId || truck.CarrierId != entity.CarrierId)
            throw new ValidationException("Fleet", "Xe và tài xế phải thuộc công ty của booking.");

        // Validate Container
        var containerIds = dto.ContainerIds ?? new List<Guid>();
        if (!containerIds.Any() && !string.IsNullOrWhiteSpace(dto.ContainerNo))
        {
            var foundCont = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ContainerNumber == dto.ContainerNo.Trim(), cancellationToken);
            if (foundCont != null)
            {
                containerIds.Add(foundCont.Id);
            }
        }

        if (!containerIds.Any())
        {
            throw new ValidationException("ContainerIds", "Phải chỉ định ít nhất một container hợp lệ cho lịch hẹn.");
        }

        var existingContainers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .AsNoTracking()
            .Where(c => containerIds.Contains(c.Id))
            .ToListAsync(cancellationToken);
        if (existingContainers.Count != containerIds.Count)
        {
            throw new ValidationException("ContainerIds", "Một hoặc nhiều container được chỉ định không tồn tại trong hệ thống cảng.");
        }

        // Check overlapping bookings
        var overlapping = await _context.Set<Domain.Entities.Booking>()
            .AsNoTracking()
            .AnyAsync(b => b.Id != entity.Id &&
                           b.Status != BookingStatus.Canceled &&
                           b.Status != BookingStatus.Completed &&
                           b.Status != BookingStatus.Expired &&
                           b.Status != BookingStatus.Rejected &&
                           (b.DriverId == dto.DriverId || b.TruckId == dto.TruckId) &&
                           b.AppointmentStart < entity.AppointmentEnd &&
                           b.AppointmentEnd > entity.AppointmentStart, cancellationToken);
        if (overlapping)
        {
            throw new ValidationException("Overlap", "Tài xế hoặc xe đầu kéo đã có một lịch hẹn đặt chỗ khác đang hoạt động trong khung giờ bị trùng lặp này.");
        }

        entity.DriverId = dto.DriverId;
        entity.DriverName = driver.FullName;
        entity.TruckId = dto.TruckId;
        entity.VehicleId = dto.TruckId;
        entity.VehiclePlate = truck.PlateNumber;

        if (!containerIds.OrderBy(x=>x).SequenceEqual(entity.BookingContainers.Select(x=>x.ContainerId).OrderBy(x=>x)))
            throw new ValidationException("ContainerIds", "Container phải khớp bản khai đã gắn với booking.");

        await LockBookingFleetAsync(dto.TruckId, dto.DriverId, cancellationToken);
        await EnsureBookingFleetAsync(entity.CarrierId, dto.TruckId, dto.DriverId,
            entity.AppointmentStart, entity.AppointmentEnd, entity.Id, cancellationToken);

        await CaptureFleetDetailsAsync(entity,cancellationToken);
        entity.MarkReady();

        await _repository.UpdateAsync(entity, cancellationToken);
        await fleetTransaction.CommitAsync(cancellationToken);

        // Send notification
        try
        {
            await _notificationService.SendAsync(new SendNotificationDto
            {
                RecipientId = entity.DriverId ?? entity.CarrierId,
                Title = $"Lệnh điều xe Booking {entity.BookingCode} đã sẵn sàng",
                Message = $"Bạn đã được phân công chuyến xe {entity.VehiclePlate} chở cont cho Booking {entity.BookingCode}. Lịch hẹn: {entity.AppointmentStart:HH:mm dd/MM/yyyy}. Trạng thái: Ready.",
                Type = NotificationType.BookingApproved,
                Severity = NotificationSeverity.Success,
                ReferenceId = entity.BookingCode
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for ready booking {BookingCode}", entity.BookingCode);
        }

        var assignDto = MapToDto(entity);
        await EnrichBookingDtosAsync(new List<BookingDto> { assignDto }, cancellationToken);
        return assignDto;
    }

    public async Task<AvailableFleetResourcesDto> GetAvailableResourcesAsync(Guid? carrierId = null, CancellationToken cancellationToken = default)
    {
        if (!carrierId.HasValue || carrierId == Guid.Empty) return new AvailableFleetResourcesDto();
        var start = DateTime.UtcNow;
        return await LoadBookingFleetAsync(carrierId.Value, start, start.AddHours(2), null, cancellationToken);
    }

    public async Task<FleetRecommendationDto> RecommendFleetAsync(Guid? containerId = null, string? bookingType = null, Guid? carrierId = null, CancellationToken cancellationToken = default, Guid? declarationId = null, string? yardCode = null, Guid? bookingId = null)
    {
        if (!carrierId.HasValue) throw new ValidationException("Carrier", "Chọn công ty vận tải trước khi lập phương án.");
        // Legacy callers receive an explicitly provisional window, without invented congestion/discount claims.
        var start = DateTime.UtcNow.AddHours(1);
        var end = start.AddHours(2);
        if (bookingId.HasValue)
        {
            var booking = await _repository.GetByIdAsync(bookingId.Value, cancellationToken);
            if (booking == null || booking.CarrierId != carrierId) throw new UnauthorizedException("Booking không thuộc công ty.");
            start = booking.AppointmentStart.ToUniversalTime();
            end = booking.AppointmentEnd.ToUniversalTime();
        }
        var plan = await PlanBookingAsync(carrierId.Value, new BookingPlanningRequest {
            BookingId=bookingId, ContainerId=containerId ?? Guid.Empty, DeclarationId=declarationId, YardCode=yardCode,
            BookingType=bookingType ?? "Dropoff", AppointmentStart=start, AppointmentEnd=end
        }, cancellationToken);
        var truck = plan.Fleet.Trucks.FirstOrDefault(t=>t.Id==plan.Fleet.RecommendedTruckId);
        var driver = plan.Fleet.Drivers.FirstOrDefault(d=>d.Id==plan.Fleet.RecommendedDriverId);
        return new FleetRecommendationDto {
            ContainerId=plan.Source.ContainerId, ContainerNumber=plan.Source.ContainerNumber,
            ContainerGrossWeightTon=(plan.Source.GrossWeightKg ?? 0)/1000m,
            ContainerSize=plan.Source.ContainerTypeCode, CargoType=plan.Source.CargoType ?? "",
            RecommendedTruckId=truck?.Id, RecommendedTruckPlate=truck?.PlateNumber, TruckMaxPayloadTon=truck?.MaxPayloadTon ?? 0,
            RecommendedDriverId=driver?.Id, RecommendedDriverName=driver?.FullName, DriverLicense=driver?.LicenseClass,
            DriverPhone=driver?.Phone,
            PayloadRatio=truck == null || truck.MaxPayloadTon <= 0 ? 0 : Math.Round((plan.Source.GrossWeightKg ?? 0)/(truck.MaxPayloadTon*10),1),
            PayloadStatus="NotEvaluated", PayloadSeverity="info",
            PayloadMessage=plan.Fleet.Message, RecommendedDate=start.ToLocalTime().ToString("yyyy-MM-dd"),
            RecommendedStartTime=start.ToLocalTime().ToString("HH:mm"), RecommendedEndTime=end.ToLocalTime().ToString("HH:mm"),
            SlotCongestionStatus="Chưa có dữ liệu năng lực khung giờ",
            SlotAdvice="Phương án tham khảo; dùng khung giờ thực tế trên Booking để kiểm tra lại."
        };
    }

    public Task<PayloadEvaluationDto> EvaluatePayloadAsync(Guid? containerId = null, Guid? truckId = null, decimal? customGrossWeightTon = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PayloadEvaluationDto
        {
            ContainerId = containerId,
            TruckId = truckId,
            ContainerGrossWeightTon = customGrossWeightTon ?? 0,
            Status = "NotEvaluated",
            Severity = "info",
            WarningMessage = "Không đánh giá tải trọng phương tiện; Booking chọn xe theo lịch rảnh.",
            IsSafe = false
        });
    }

    private static string NormalizeYardCode(string? code) =>
        System.Text.RegularExpressions.Regex.Replace((code ?? "").Trim().ToUpperInvariant(), "^YARD-", "TC-");

    private async Task EnrichBookingDtosAsync(List<BookingDto> dtos, CancellationToken cancellationToken)
    {
        if (!dtos.Any()) return;
        foreach(var dto in dtos) SignFleetDocuments(dto.VehicleDetails,dto.DriverDetails);

        var driverIds = dtos.Where(d => d.DriverId.HasValue && d.DriverId.Value != Guid.Empty).Select(d => d.DriverId!.Value).Distinct().ToList();
        var truckIds = dtos.Where(d => d.TruckId.HasValue && d.TruckId.Value != Guid.Empty).Select(d => d.TruckId!.Value).Distinct().ToList();
        var containerIds = dtos.SelectMany(d => d.ContainerIds).Where(c => c != Guid.Empty).Distinct().ToList();

        var driversDict = driverIds.Any()
            ? await _context.Set<NexusPort.Modules.Driver.Domain.Entities.Driver>()
                .AsNoTracking()
                .Where(d => driverIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.FullName, cancellationToken)
            : new Dictionary<Guid, string>();

        var trucksDict = truckIds.Any()
            ? await _context.Set<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>()
                .AsNoTracking()
                .Where(v => truckIds.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, v => v.PlateNumber, cancellationToken)
            : new Dictionary<Guid, string>();

        var containersDict = containerIds.Any()
            ? await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
                .AsNoTracking()
                .Where(c => containerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.ContainerNumber, cancellationToken)
            : new Dictionary<Guid, string>();

        var visitIds = dtos.SelectMany(d => d.ContainerSelections).Where(s => s.ContainerVisitId.HasValue)
            .Select(s => s.ContainerVisitId!.Value).Distinct().ToArray();
        var visitInfo = visitIds.Length == 0 ? new List<BookingVisitInfo>() : await _context.Database.SqlQueryRaw<BookingVisitInfo>(
            "SELECT v.id AS \"Id\", v.pickup_code AS \"YardCode\", c.container_no AS \"ContainerNumber\",ct.code AS \"ContainerTypeCode\",cargo.name_vi AS \"CargoTypeName\" FROM container_visits v JOIN containers c ON c.id=v.container_id JOIN container_types ct ON ct.id=c.container_type_id LEFT JOIN container_cargo_types cargo ON cargo.code::text=v.cargo_type::text WHERE v.id=ANY({0})", visitIds)
            .ToListAsync(cancellationToken);
        foreach (var booking in dtos)
            foreach (var selection in booking.ContainerSelections)
            {
                var info = visitInfo.FirstOrDefault(v => v.Id == selection.ContainerVisitId);
                if (info == null) continue;
                selection.YardCode = booking.BookingType == BookingType.Dropoff ? info.YardCode : null;
                selection.ContainerNumber = info.ContainerNumber;
                selection.ContainerTypeCode = info.ContainerTypeCode;
                selection.CargoTypeName = info.CargoTypeName;
            }

        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.DriverName) && dto.DriverId.HasValue && driversDict.TryGetValue(dto.DriverId.Value, out var driverName))
            {
                dto.DriverName = driverName;
            }

            if (string.IsNullOrWhiteSpace(dto.VehiclePlate) && dto.TruckId.HasValue && trucksDict.TryGetValue(dto.TruckId.Value, out var plate))
            {
                dto.VehiclePlate = plate;
            }

            if (dto.ContainerIds.Any())
            {
                dto.ContainerNumbers = dto.ContainerIds
                    .Select(cid => containersDict.TryGetValue(cid, out var cNum) ? cNum : cid.ToString())
                    .ToList();
            }

            if (dto.Status == BookingStatus.Ready || dto.Status == BookingStatus.CheckedIn || dto.Status == BookingStatus.Completed)
            {
                dto.PaymentStatus = "Paid";
            }
        }
    }

    public async Task<BookingPaymentInfoDto> GetPaymentInfoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var booking = await _repository.GetByIdWithContainersAsync(id, cancellationToken);
        if (booking == null)
        {
            throw new NotFoundException("Booking", id.ToString());
        }

        // Determine fee based on booking container size
        bool is40Ft = false;
        if (booking.BookingContainers.Any())
        {
            var cIds = booking.BookingContainers.Select(bc => bc.ContainerId).ToList();
            var contNumbers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
                .Where(c => cIds.Contains(c.Id))
                .Select(c => c.ContainerNumber)
                .ToListAsync(cancellationToken);
            is40Ft = contNumbers.Any(n => n != null && (n.Contains("40") || n.Contains("45") || n.StartsWith("TEMU")));
        }

        decimal handlingFee = is40Ft ? 650000m : 450000m;
        decimal weighingFee = 50000m;
        decimal infrastructureFee = 40000m;
        decimal subtotal = handlingFee + weighingFee + infrastructureFee;
        decimal taxAmount = Math.Round(subtotal * 0.08m, 0);
        decimal totalAmount = subtotal + taxAmount;

        // Check if invoice or payment already exists in DB
        string status = (booking.Status == BookingStatus.Completed || booking.Status == BookingStatus.CheckedIn) ? "Paid" : "Unpaid";
        string? invoiceNo = null;
        string? txnRef = null;
        DateTime? paidAt = null;

        try
        {
            var conn = _context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT i.invoice_no, p.transaction_ref, p.paid_at, p.status::text as payment_status
                FROM invoices i
                LEFT JOIN payments p ON p.invoice_id = i.id
                WHERE i.invoice_no = @invNo OR i.invoice_no LIKE @invPattern
                LIMIT 1";
            
            var p1 = cmd.CreateParameter();
            p1.ParameterName = "@invNo";
            p1.Value = $"INV-NP-{booking.BookingCode}";
            cmd.Parameters.Add(p1);

            var p2 = cmd.CreateParameter();
            p2.ParameterName = "@invPattern";
            p2.Value = $"%{booking.BookingCode}%";
            cmd.Parameters.Add(p2);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                invoiceNo = reader.IsDBNull(0) ? null : reader.GetString(0);
                txnRef = reader.IsDBNull(1) ? null : reader.GetString(1);
                paidAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
                var pStat = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (pStat == "success" || pStat == "paid")
                {
                    status = "Paid";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not query invoices/payments table for booking {BookingCode}", booking.BookingCode);
        }

        string transferContent = $"NP {booking.BookingCode}";
        string qrUrl = $"https://img.vietqr.io/image/MB-190388668899-compact2.png?amount={totalAmount:0}&addInfo={Uri.EscapeDataString(transferContent)}&accountName={Uri.EscapeDataString("CONG TY CP CANG QUOC TE NEXUSPORT")}";

        return new BookingPaymentInfoDto
        {
            BookingId = booking.Id,
            BookingCode = booking.BookingCode,
            PaymentStatus = status,
            HandlingFee = handlingFee,
            WeighingFee = weighingFee,
            InfrastructureFee = infrastructureFee,
            Subtotal = subtotal,
            TaxAmount = taxAmount,
            TotalAmount = totalAmount,
            InvoiceNo = invoiceNo ?? $"INV-NP-{booking.BookingCode}",
            TransactionRef = txnRef ?? (status == "Paid" ? $"TXN-NP-{booking.BookingCode}" : null),
            PaidAt = paidAt ?? (status == "Paid" ? booking.CreatedAt : null),
            PaymentMethod = "VietQR",
            BankName = "MB Bank (Ngân Hàng TMCP Quân Đội)",
            AccountNo = "190388668899",
            AccountName = "CÔNG TY CP CẢNG QUỐC TẾ NEXUSPORT",
            TransferContent = transferContent,
            QrUrl = qrUrl
        };
    }

    public async Task<BookingPaymentInfoDto> ProcessPaymentAsync(Guid id, ProcessBookingPaymentDto dto, CancellationToken cancellationToken = default)
    {
        var booking = await _repository.GetByIdWithContainersAsync(id, cancellationToken);
        if (booking == null)
        {
            throw new NotFoundException("Booking", id.ToString());
        }

        // Get payment calculation
        var info = await GetPaymentInfoAsync(id, cancellationToken);
        var txnRef = !string.IsNullOrWhiteSpace(dto.TransactionRef) 
            ? dto.TransactionRef 
            : $"TXN-NP-{DateTime.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";
        var invoiceNo = $"INV-NP-{booking.BookingCode}";
        var now = DateTime.UtcNow;

        try
        {
            var conn = _context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(cancellationToken);
            }

            // 1. Create or get invoice
            var invoiceId = Guid.NewGuid();
            using (var cmdInv = conn.CreateCommand())
            {
                cmdInv.CommandText = @"
                    INSERT INTO invoices (id, carrier_id, invoice_no, status, subtotal, tax_amount, total_amount, paid_amount, issued_by, issued_at, due_date, created_at)
                    VALUES (@id, @carrierId, @invNo, 'paid'::invoice_status, @subtotal, @tax, @total, @paid, @issuedBy, @issuedAt, @dueDate, @createdAt)
                    ON CONFLICT (id) DO NOTHING;";
                
                var pId = cmdInv.CreateParameter(); pId.ParameterName = "@id"; pId.Value = invoiceId; cmdInv.Parameters.Add(pId);
                var pCarrier = cmdInv.CreateParameter(); pCarrier.ParameterName = "@carrierId"; pCarrier.Value = booking.CarrierId; cmdInv.Parameters.Add(pCarrier);
                var pInvNo = cmdInv.CreateParameter(); pInvNo.ParameterName = "@invNo"; pInvNo.Value = invoiceNo; cmdInv.Parameters.Add(pInvNo);
                var pSub = cmdInv.CreateParameter(); pSub.ParameterName = "@subtotal"; pSub.Value = info.Subtotal; cmdInv.Parameters.Add(pSub);
                var pTax = cmdInv.CreateParameter(); pTax.ParameterName = "@tax"; pTax.Value = info.TaxAmount; cmdInv.Parameters.Add(pTax);
                var pTot = cmdInv.CreateParameter(); pTot.ParameterName = "@total"; pTot.Value = info.TotalAmount; cmdInv.Parameters.Add(pTot);
                var pPaid = cmdInv.CreateParameter(); pPaid.ParameterName = "@paid"; pPaid.Value = info.TotalAmount; cmdInv.Parameters.Add(pPaid);
                var pBy = cmdInv.CreateParameter(); pBy.ParameterName = "@issuedBy"; pBy.Value = booking.CarrierId; cmdInv.Parameters.Add(pBy);
                var pAt = cmdInv.CreateParameter(); pAt.ParameterName = "@issuedAt"; pAt.Value = now; cmdInv.Parameters.Add(pAt);
                var pDue = cmdInv.CreateParameter(); pDue.ParameterName = "@dueDate"; pDue.Value = now.Date; cmdInv.Parameters.Add(pDue);
                var pCr = cmdInv.CreateParameter(); pCr.ParameterName = "@createdAt"; pCr.Value = now; cmdInv.Parameters.Add(pCr);

                await cmdInv.ExecuteNonQueryAsync(cancellationToken);
            }

            // 2. Insert payment record
            using (var cmdPay = conn.CreateCommand())
            {
                cmdPay.CommandText = @"
                    INSERT INTO payments (id, invoice_id, method, status, amount, transaction_ref, paid_at, created_at)
                    VALUES (@id, @invoiceId, 'vietqr'::payment_method, 'success'::payment_status, @amount, @txnRef, @paidAt, @createdAt);";

                var pId = cmdPay.CreateParameter(); pId.ParameterName = "@id"; pId.Value = Guid.NewGuid(); cmdPay.Parameters.Add(pId);
                var pInvId = cmdPay.CreateParameter(); pInvId.ParameterName = "@invoiceId"; pInvId.Value = invoiceId; cmdPay.Parameters.Add(pInvId);
                var pAmt = cmdPay.CreateParameter(); pAmt.ParameterName = "@amount"; pAmt.Value = info.TotalAmount; cmdPay.Parameters.Add(pAmt);
                var pTxn = cmdPay.CreateParameter(); pTxn.ParameterName = "@txnRef"; pTxn.Value = txnRef; cmdPay.Parameters.Add(pTxn);
                var pPaidAt = cmdPay.CreateParameter(); pPaidAt.ParameterName = "@paidAt"; pPaidAt.Value = now; cmdPay.Parameters.Add(pPaidAt);
                var pCr = cmdPay.CreateParameter(); pCr.ParameterName = "@createdAt"; pCr.Value = now; cmdPay.Parameters.Add(pCr);

                await cmdPay.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write to raw invoices/payments table: {Message}", ex.Message);
        }

        // 3. Update booking status: If Approved or Pending -> Ready (Ready for Gate-In)
        if (booking.Status == BookingStatus.Approved || booking.Status == BookingStatus.Pending)
        {
            booking.MarkReady();
            await _repository.UpdateAsync(booking, cancellationToken);
        }

        // 4. Send Notification to Carrier
        await _notificationService.SendAsync(new SendNotificationDto
        {
            RecipientId = booking.CarrierId,
            Title = $"Thanh toán thành công Booking {booking.BookingCode}",
            Message = $"Đã quyết toán cước cảng {info.TotalAmount:N0} VNĐ cho Booking {booking.BookingCode} qua VietQR Napas 24/7. Vé điện tử e-Pass vào cổng đã sẵn sàng!",
            Type = NotificationType.BookingApproved,
            Severity = NotificationSeverity.Success,
            ReferenceId = booking.BookingCode
        }, cancellationToken);

        info.PaymentStatus = "Paid";
        info.TransactionRef = txnRef;
        info.PaidAt = now;
        info.InvoiceNo = invoiceNo;

        return info;
    }

    public async Task<IReadOnlyList<BookingOperationDto>> GetOperationsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var ops = await _context.Set<Domain.Entities.BookingOperation>()
            .Include(o => o.Documents)
            .Where(o => o.BookingId == bookingId)
            .OrderBy(o => o.SequenceNumber)
            .ToListAsync(cancellationToken);

        return ops.Select(MapOperationToDto).ToList();
    }

    public async Task<BookingOperationDto> UpdateOperationStatusAsync(Guid bookingId, Guid operationId, UpdateBookingOperationStatusDto dto, Guid? updatedBy = null, CancellationToken cancellationToken = default)
    {
        var booking = await _context.Set<Domain.Entities.Booking>()
            .Include(b => b.BookingOperations)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        if (booking == null) throw new NotFoundException("Booking", bookingId);

        var op = booking.BookingOperations.FirstOrDefault(o => o.Id == operationId);
        if (op == null) throw new NotFoundException("BookingOperation", operationId);

        op.Status = dto.Status;
        if (!string.IsNullOrWhiteSpace(dto.Notes)) op.VerificationNotes = dto.Notes;
        if (!string.IsNullOrWhiteSpace(dto.EirNumber))
        {
            op.EirNumber = dto.EirNumber;
            op.EirIssuedAt = DateTime.UtcNow;
        }
        if (!string.IsNullOrWhiteSpace(dto.ContainerCondition)) op.ContainerCondition = dto.ContainerCondition;
        if (updatedBy.HasValue)
        {
            op.VerifiedBy = updatedBy.Value;
            op.VerifiedAt = DateTime.UtcNow;
        }

        booking.UpdateDerivedStatus();
        await _context.SaveChangesAsync(cancellationToken);

        return MapOperationToDto(op);
    }

    public async Task<BookingDocumentDto> AddDocumentAsync(Guid bookingId, CreateBookingDocumentDto dto, CancellationToken cancellationToken = default)
    {
        var booking = await _repository.GetByIdAsync(bookingId, cancellationToken);
        if (booking == null) throw new NotFoundException("Booking", bookingId);

        var doc = new Domain.Entities.BookingDocument(
            bookingId,
            dto.DocumentType,
            dto.DocumentNumber,
            dto.FileUrl,
            dto.FileName,
            dto.BookingOperationId);
        doc.Issuer = dto.Issuer;
        doc.FileSizeBytes = dto.FileSizeBytes;
        doc.ExpiryDate = dto.ExpiryDate;

        _context.Set<Domain.Entities.BookingDocument>().Add(doc);
        await _context.SaveChangesAsync(cancellationToken);

        return MapDocumentToDto(doc);
    }

    public async Task<BookingDocumentDto> VerifyDocumentAsync(Guid bookingId, Guid documentId, VerifyBookingDocumentDto dto, Guid verifiedBy, CancellationToken cancellationToken = default)
    {
        var doc = await _context.Set<Domain.Entities.BookingDocument>()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.BookingId == bookingId, cancellationToken);

        if (doc == null) throw new NotFoundException("BookingDocument", documentId);

        if (dto.IsApproved)
        {
            doc.Verify(verifiedBy);
        }
        else
        {
            doc.Reject(verifiedBy, dto.Reason ?? "Chứng từ bị từ chối.");
        }

        await _context.SaveChangesAsync(cancellationToken);
        return MapDocumentToDto(doc);
    }

    public Task<IReadOnlyList<DocumentRequirementSpec>> GetDocumentRequirementsAsync(string operationType, string cargoStatus, string cargoType, CancellationToken cancellationToken = default)
    {
        var specs = _documentRequirementPolicy.GetRequiredDocuments(operationType, cargoStatus, cargoType);
        return Task.FromResult(specs);
    }

    public Task<OperationCombinationValidationResult> ValidateCombinationAsync(IReadOnlyList<CreateBookingOperationDto> operations, CancellationToken cancellationToken = default)
    {
        var result = _compatibilityPolicy.ValidateCombination(operations);
        return Task.FromResult(result);
    }

    private static BookingDto MapToDto(Domain.Entities.Booking entity)
    {
        return new BookingDto
        {
            Id = entity.Id,
            VehicleDetails=ReadFleetDetails<BookingVehicleDetailsDto>(entity.VehicleSnapshot),
            DriverDetails=ReadFleetDetails<BookingDriverDetailsDto>(entity.DriverSnapshot),
            TrailerNumber=entity.TrailerNumber,
            CarrierId = entity.CarrierId,
            DriverId = entity.DriverId,
            TruckId = entity.TruckId,
            BookingCode = entity.BookingCode,
            BookingType = entity.BookingType,
            Status = entity.Status,
            AppointmentStart = entity.AppointmentStart,
            AppointmentEnd = entity.AppointmentEnd,
            ApprovedBy = entity.ApprovedBy,
            ApprovedAt = entity.ApprovedAt,
            RejectedReason = entity.RejectedReason,
            CanceledAt = entity.CanceledAt,
            CreatedAt = entity.CreatedAt,
            ContainerIds = entity.BookingContainers?.Select(bc => bc.ContainerId).ToList() ?? new List<Guid>(),
            ContainerSelections = (entity.BookingContainers ?? new List<BookingContainer>()).Select(x=>new BookingContainerSelectionDto { ContainerId=x.ContainerId, DeclarationId=x.DeclarationId??Guid.Empty, ContainerVisitId=x.ContainerVisitId }).ToList(),

            IsCombined = entity.IsCombined,
            TripStatus = entity.TripStatus ?? "Scheduled",
            CustomerId = entity.CustomerId,
            Operations = entity.BookingOperations?.Select(MapOperationToDto).OrderBy(o => o.SequenceNumber).ToList() ?? new List<BookingOperationDto>(),
            Documents = entity.BookingDocuments?.Select(MapDocumentToDto).ToList() ?? new List<BookingDocumentDto>(),

            Description = entity.Description,
            VehiclePlate = entity.VehiclePlate,
            VehicleId = entity.VehicleId,
            DriverName = entity.DriverName,
            ValidFrom = entity.ValidFrom ?? entity.AppointmentStart,
            ValidTo = entity.ValidTo ?? entity.AppointmentEnd,
            PaymentStatus = (entity.Status == BookingStatus.Ready || entity.Status == BookingStatus.CheckedIn || entity.Status == BookingStatus.Completed) ? "Paid" : "Unpaid",
            TotalAmount = 583200m,

            IsReeferPowerRequired = entity.IsReeferPowerRequired,
            TargetTemperature = entity.TargetTemperature,
            TemperatureUnit = entity.TemperatureUnit,
            TempTolerance = entity.TempTolerance,
            ReeferPlugId = entity.ReeferPlugId,
            ReeferPlugCode = entity.ReeferPlugCode,
            ReeferPowerStatus = entity.ReeferPowerStatus ?? (entity.IsReeferPowerRequired ? "Reserved" : "None"),
            RequiredPowerKw = entity.RequiredPowerKw ?? (entity.IsReeferPowerRequired ? 15.0m : null)
        };
    }

    private static BookingOperationDto MapOperationToDto(Domain.Entities.BookingOperation op)
    {
        return new BookingOperationDto
        {
            Id = op.Id,
            BookingId = op.BookingId,
            OperationType = op.OperationType,
            SequenceNumber = op.SequenceNumber,
            ExpectedGateDirection = op.ExpectedGateDirection,
            Status = op.Status,
            ContainerId = op.ContainerId,
            ContainerNumber = op.ContainerNumber,
            ContainerIsoSize = op.ContainerIsoSize,
            CargoStatus = op.CargoStatus,
            CargoType = op.CargoType,
            CarrierBookingNumber = op.CarrierBookingNumber,
            BillOfLadingNumber = op.BillOfLadingNumber,
            DeliveryOrderNumber = op.DeliveryOrderNumber,
            CustomsDeclarationNumber = op.CustomsDeclarationNumber,
            SealNumber = op.SealNumber,
            GrossWeightKg = op.GrossWeightKg,
            TemperatureSetpoint = op.TemperatureSetpoint,
            TemperatureUnit = op.TemperatureUnit,
            UnNumber = op.UnNumber,
            DgClass = op.DgClass,
            SpecialHandlingRequirements = op.SpecialHandlingRequirements,
            VerificationStatus = op.VerificationStatus,
            VerifiedBy = op.VerifiedBy,
            VerifiedAt = op.VerifiedAt,
            VerificationNotes = op.VerificationNotes,
            EirNumber = op.EirNumber,
            EirIssuedAt = op.EirIssuedAt,
            ContainerCondition = op.ContainerCondition,
            Documents = op.Documents?.Select(MapDocumentToDto).ToList() ?? new List<BookingDocumentDto>()
        };
    }

    private static BookingDocumentDto MapDocumentToDto(Domain.Entities.BookingDocument doc)
    {
        return new BookingDocumentDto
        {
            Id = doc.Id,
            BookingId = doc.BookingId,
            BookingOperationId = doc.BookingOperationId,
            DocumentType = doc.DocumentType,
            DocumentNumber = doc.DocumentNumber,
            FileUrl = doc.FileUrl,
            FileName = doc.FileName,
            FileSizeBytes = doc.FileSizeBytes,
            Issuer = doc.Issuer,
            VerificationStatus = doc.VerificationStatus,
            VerifiedBy = doc.VerifiedBy,
            VerifiedAt = doc.VerifiedAt,
            RejectionReason = doc.RejectionReason,
            ExpiryDate = doc.ExpiryDate,
            CreatedAt = doc.CreatedAt
        };
    }
}







public class BookingGateEntryTime
{
    public Guid BookingId { get; set; }
    public DateTime CheckedAt { get; set; }
}

public sealed class PickupCodeLink
{
    public Guid DeclarationId { get; set; }
    public Guid ContainerVisitId { get; set; }
}
public sealed class BookingVisitInfo
{
    public Guid Id { get; set; }
    public string YardCode { get; set; } = string.Empty;
    public string ContainerNumber { get; set; } = string.Empty;
    public string ContainerTypeCode { get; set; } = string.Empty;
    public string? CargoTypeName { get; set; }
}

public sealed class RecommendationCargo
{
    public Guid Id { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string? CargoType { get; set; }
    public decimal? GrossWeightKg { get; set; }
}

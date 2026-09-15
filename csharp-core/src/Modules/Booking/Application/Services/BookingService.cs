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

namespace NexusPort.Modules.Booking.Application.Services;

public class BookingService : IBookingService
{
    private readonly IBookingRepository _repository;
    private readonly IBookingValidationService _validationService;
    private readonly INotificationService _notificationService;
    private readonly AppDbContext _context;
    private readonly ILogger<BookingService> _logger;

    public BookingService(
        IBookingRepository repository,
        IBookingValidationService validationService,
        INotificationService notificationService,
        AppDbContext context,
        ILogger<BookingService> logger)
    {
        _repository = repository;
        _validationService = validationService;
        _notificationService = notificationService;
        _context = context;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BookingDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(MapToDto).ToList();
    }

    public async Task<PagedResult<BookingDto>> GetPagedAsync(BookingFilterParams filter, CancellationToken cancellationToken = default)
    {
        var pagedEntities = await _repository.GetPagedAsync(filter, cancellationToken);
        var dtos = pagedEntities.Items.Select(MapToDto).ToList();

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

        return MapToDto(entity);
    }

    public async Task<BookingDto> CreateAsync(CreateBookingDto dto, CancellationToken cancellationToken = default)
    {
        // Execute Business Validation Rules (NXP-042)
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

        entity.VehiclePlate = dto.VehiclePlate;
        entity.VehicleId = dto.VehicleId;
        entity.DriverName = dto.DriverName;
        entity.ValidFrom = dto.ValidFrom ?? dto.AppointmentStart;
        entity.ValidTo = dto.ValidTo ?? dto.AppointmentEnd;
        entity.GateType = dto.GateType ?? "GateIn";
        entity.Description = dto.Description;

        if (dto.ContainerIds != null)
        {
            foreach (var containerId in dto.ContainerIds)
            {
                entity.AddContainer(containerId);
            }
        }

        await _repository.AddAsync(entity, cancellationToken);

        // Emit real business notification to Database (NXP-044)
        await _notificationService.SendAsync(new SendNotificationDto
        {
            RecipientId = entity.CarrierId,
            Title = $"Tạo mới Booking {entity.BookingCode}",
            Message = $"Lịch hẹn {entity.BookingType} mã {entity.BookingCode} đã khởi tạo thành công lúc {DateTime.UtcNow:HH:mm dd/MM/yyyy}.",
            Type = NotificationType.BookingApproved,
            Severity = NotificationSeverity.Success,
            ReferenceId = entity.BookingCode
        }, cancellationToken);

        return MapToDto(entity);
    }

    public async Task<BookingDto> UpdateAsync(Guid id, UpdateBookingDto dto, Guid? userCarrierId = null, CancellationToken cancellationToken = default)
    {
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
            throw new ValidationException("Status", $"Booking in '{entity.Status}' status cannot be updated.");
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
            ContainerIds = dto.ContainerIds
        };

        await _validationService.ValidateBookingAsync(validationDto, cancellationToken);

        // Update properties
        entity.DriverId = dto.DriverId ?? entity.DriverId;
        entity.TruckId = dto.TruckId ?? entity.TruckId;
        entity.AppointmentStart = dto.AppointmentStart;
        entity.AppointmentEnd = dto.AppointmentEnd;

        // Update containers
        entity.BookingContainers.Clear();
        if (dto.ContainerIds != null)
        {
            foreach (var containerId in dto.ContainerIds)
            {
                entity.AddContainer(containerId);
            }
        }

        await _repository.UpdateAsync(entity, cancellationToken);

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

        return MapToDto(entity);
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
            throw new ValidationException("Status", $"Booking in '{entity.Status}' status cannot be canceled.");
        }

        if (entity.Status == BookingStatus.Canceled)
        {
            throw new ValidationException("Status", "Booking is already canceled.");
        }

        entity.Cancel();
        if (!string.IsNullOrWhiteSpace(dto?.Reason))
        {
            entity.RejectedReason = dto.Reason;
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

        return MapToDto(entity);
    }

    public async Task<IReadOnlyList<DriverContainerOperationDto>> GetDriverOperationsAsync(Guid driverId, CancellationToken cancellationToken = default)
    {
        var bookings = await _context.Set<Domain.Entities.Booking>()
            .AsNoTracking()
            .Include(x => x.BookingContainers)
            .Where(x => x.DriverId == driverId && x.Status != BookingStatus.Canceled && x.Status != BookingStatus.Completed)
            .OrderBy(x => x.AppointmentStart)
            .ToListAsync(cancellationToken);

        var completedOperationContainerIds = await _context.Set<NexusPort.Modules.Yard.Domain.Entities.YardOperationEvent>()
            .AsNoTracking()
            .Where(x => x.DriverId == driverId && x.OperationStatus == "Completed")
            .Select(x => x.ContainerId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var containerIds = bookings.SelectMany(x => x.BookingContainers.Select(bc => bc.ContainerId))
            .Where(completedOperationContainerIds.Contains)
            .Distinct()
            .ToList();
        var containers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .AsNoTracking()
            .Where(x => containerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return bookings.SelectMany(booking => booking.BookingContainers
            .Where(link => containers.ContainsKey(link.ContainerId))
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
            .AnyAsync(x => x.Id == driverId && x.Status == DriverStatus.active, cancellationToken);
        if (!driverExists)
            throw new UnauthorizedException("Only an active assigned driver can confirm a container.");

        var booking = await _context.Set<Domain.Entities.Booking>()
            .Include(x => x.BookingContainers)
            .Where(x => x.DriverId == driverId)
            .Where(x => x.BookingContainers.Any(link => link.ContainerId == dto.ContainerId))
            .Where(x => x.Status != BookingStatus.Canceled)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (booking == null)
            throw new UnauthorizedException("The container is not assigned to the authenticated driver.");

        var operationCompleted = await _context.Set<NexusPort.Modules.Yard.Domain.Entities.YardOperationEvent>()
            .AnyAsync(x => x.DriverId == driverId && x.ContainerId == dto.ContainerId && x.OperationStatus == "Completed", cancellationToken);
        if (!operationCompleted)
            throw new ValidationException("Container", "The assigned yard operation has not been completed yet.");

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

        var normalizedCondition = string.IsNullOrWhiteSpace(dto.Condition) ? "OK" : dto.Condition.Trim().ToUpperInvariant();
        if (normalizedCondition is not ("OK" or "DAMAGED" or "ISSUE"))
            throw new ValidationException("Condition", "Condition must be OK, DAMAGED, or ISSUE.");

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            const string readyForGateOut = "ReadyForGateOut";
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

    private static BookingDto MapToDto(Domain.Entities.Booking entity)
    {
        return new BookingDto
        {
            Id = entity.Id,
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

            Description = entity.Description,
            VehiclePlate = entity.VehiclePlate,
            VehicleId = entity.VehicleId,
            DriverName = entity.DriverName,
            ValidFrom = entity.ValidFrom ?? entity.AppointmentStart,
            ValidTo = entity.ValidTo ?? entity.AppointmentEnd,
            GateType = entity.GateType
        };
    }
}

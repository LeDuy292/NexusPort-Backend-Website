using NexusPort.Infrastructure.Notifications.DTOs;
using NexusPort.Infrastructure.Notifications.Enums;
using NexusPort.Infrastructure.Notifications.Interfaces;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Modules.Booking.Domain.Entities;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Shared.Exceptions;
using NexusPort.Shared.Results;

namespace NexusPort.Modules.Booking.Application.Services;

public class BookingService : IBookingService
{
    private readonly IBookingRepository _repository;
    private readonly IBookingValidationService _validationService;
    private readonly INotificationService _notificationService;

    public BookingService(
        IBookingRepository repository,
        IBookingValidationService validationService,
        INotificationService notificationService)
    {
        _repository = repository;
        _validationService = validationService;
        _notificationService = notificationService;
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

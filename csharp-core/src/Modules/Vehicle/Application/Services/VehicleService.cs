using Microsoft.Extensions.Logging;
using NexusPort.Modules.Vehicle.Application.DTOs;
using NexusPort.Modules.Vehicle.Application.Interfaces;
using NexusPort.Infrastructure.ExternalServices;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Vehicle.Application.Services;

public class VehicleService : IVehicleService
{
    private readonly IVehicleRepository _repository;
    private readonly IMessageBrokerService _messageBroker;
    private readonly ILogger<VehicleService> _logger;
    private readonly IS3StorageService _s3StorageService;
    private readonly AppDbContext _context;

    public VehicleService(IVehicleRepository repository, IMessageBrokerService messageBroker, ILogger<VehicleService> logger, IS3StorageService s3StorageService, AppDbContext context)
    {
        _repository = repository;
        _messageBroker = messageBroker;
        _logger = logger;
        _s3StorageService = s3StorageService;
        _context = context;
    }

    public async Task<IReadOnlyList<VehicleDto>> GetAllAsync(VehicleFilterDto filter, CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(filter, cancellationToken);
        return entities.Select(e =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new VehicleDto
            {
                Id = e.Id,
                CarrierId = e.CarrierId,
                DriverId = e.DriverId,
                PlateNumber = e.PlateNumber,
                Status = e.Status.ToString(),
                VehicleType = e.VehicleType,
                Description = e.Description,
                RegistrationImageUrl = filter.IncludeDocuments ? _s3StorageService.GetPresignedUrl(e.RegistrationImageUrl) : null,
                HasPhoto = !string.IsNullOrWhiteSpace(e.PhotoUrl),
                PhotoUrl = filter.IncludePhotos ? _s3StorageService.GetPresignedUrl(e.PhotoUrl) : null,
                CurrentLocation = e.CurrentLocation,
                CreatedAt = e.CreatedAt
            };
        }).ToList();
    }

    public async Task<VehicleDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new VehicleDto
        {
            Id = entity.Id,
            CarrierId = entity.CarrierId,
            DriverId = entity.DriverId,
            PlateNumber = entity.PlateNumber,
            Status = entity.Status.ToString(),
            VehicleType = entity.VehicleType,
            Description = entity.Description,
            RegistrationImageUrl = _s3StorageService.GetPresignedUrl(entity.RegistrationImageUrl),
            PhotoUrl = _s3StorageService.GetPresignedUrl(entity.PhotoUrl),
            CurrentLocation = entity.CurrentLocation,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<VehicleDto> CreateAsync(Guid carrierId, CreateVehicleDto dto, CancellationToken cancellationToken = default)
    {
        if (await _repository.ExistsByPlateNumberAsync(dto.PlateNumber, null, cancellationToken))
        {
            throw new InvalidOperationException("A vehicle with this license plate already exists.");
        }

        var entity = new NexusPort.Modules.Vehicle.Domain.Entities.Vehicle(
            carrierId: carrierId,
            plateNumber: dto.PlateNumber
        );

        if (!string.IsNullOrWhiteSpace(dto.VehicleType)) 
        {
            entity.VehicleType = dto.VehicleType;
        }
        entity.Description = dto.Description;
        if (!string.IsNullOrWhiteSpace(dto.RegistrationImageUrl))
        {
            entity.RegistrationImageUrl = dto.RegistrationImageUrl;
        }
        if (!string.IsNullOrWhiteSpace(dto.PhotoUrl))
        {
            entity.PhotoUrl = dto.PhotoUrl;
        }
        if (!string.IsNullOrWhiteSpace(dto.CurrentLocation))
        {
            entity.CurrentLocation = dto.CurrentLocation;
        }

        await _repository.AddAsync(entity, cancellationToken);
        return new VehicleDto
        {
            Id = entity.Id,
            CarrierId = entity.CarrierId,
            DriverId = entity.DriverId,
            PlateNumber = entity.PlateNumber,
            Status = entity.Status.ToString(),
            VehicleType = entity.VehicleType,
            Description = entity.Description,
            RegistrationImageUrl = _s3StorageService.GetPresignedUrl(entity.RegistrationImageUrl),
            PhotoUrl = _s3StorageService.GetPresignedUrl(entity.PhotoUrl),
            CurrentLocation = entity.CurrentLocation,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<VehicleDto> UpdateAsync(Guid id, UpdateVehicleDto dto, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Vehicle not found.");

        if (await _repository.ExistsByPlateNumberAsync(dto.PlateNumber, id, cancellationToken))
        {
            throw new InvalidOperationException("A vehicle with this license plate already exists.");
        }

        entity.PlateNumber = dto.PlateNumber;
        if (!string.IsNullOrWhiteSpace(dto.VehicleType)) 
        {
            entity.VehicleType = dto.VehicleType;
        }
        entity.Description = dto.Description;
        if (!string.IsNullOrWhiteSpace(dto.RegistrationImageUrl))
        {
            entity.RegistrationImageUrl = dto.RegistrationImageUrl;
        }
        if (!string.IsNullOrWhiteSpace(dto.PhotoUrl))
        {
            entity.PhotoUrl = dto.PhotoUrl;
        }
        if (dto.CurrentLocation != null)
        {
            entity.CurrentLocation = dto.CurrentLocation;
        }

        await _repository.UpdateAsync(entity, cancellationToken);

        return new VehicleDto
        {
            Id = entity.Id,
            CarrierId = entity.CarrierId,
            DriverId = entity.DriverId,
            PlateNumber = entity.PlateNumber,
            Status = entity.Status.ToString(),
            VehicleType = entity.VehicleType,
            Description = entity.Description,
            RegistrationImageUrl = _s3StorageService.GetPresignedUrl(entity.RegistrationImageUrl),
            PhotoUrl = _s3StorageService.GetPresignedUrl(entity.PhotoUrl),
            CurrentLocation = entity.CurrentLocation,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task ToggleStatusAsync(Guid id, string status, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Vehicle not found.");

        if (!Enum.TryParse<NexusPort.Modules.Vehicle.Domain.Enums.TruckStatus>(status, true, out var parsedStatus))
        {
            throw new ArgumentException($"Invalid status value: '{status}'. Valid values: active, inactive, maintenance.");
        }

        entity.Status = parsedStatus;
        await _repository.UpdateAsync(entity, cancellationToken);
        await PublishStatusAsync(entity.Id, entity.Status.ToString(), entity.PlateNumber, cancellationToken);
    }

    public async Task AssignDriverAsync(Guid id, AssignDriverDto dto, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        // Serialize assignments, including two requests trying to assign the same driver to different vehicles.
        await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM trucks WHERE id={0} FOR UPDATE", id).ToListAsync(cancellationToken);
        if (dto.DriverId.HasValue)
            await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM drivers WHERE id={0} FOR UPDATE", dto.DriverId.Value).ToListAsync(cancellationToken);
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Vehicle not found.");
        await _context.Entry(entity).ReloadAsync(cancellationToken);

        if (dto.DriverId.HasValue)
        {
            var eligible = await _context.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS(SELECT 1 FROM drivers d LEFT JOIN users u ON u.id=d.user_id WHERE d.id={0} AND d.carrier_id={1}
                  AND d.status::text IN ('active','vehicle_received','transport_completed')
                  AND (d.license_expiry_date IS NULL OR d.license_expiry_date>=now())
                  AND u.is_active AND NOT u.invitation_pending AND u.email_verified_at IS NOT NULL) AS "Value"
                """, dto.DriverId.Value, entity.CarrierId).SingleAsync(cancellationToken);
            if (!eligible) throw new ValidationException("DriverId", "Tài xế phải thuộc công ty, đã xác nhận email và đang sẵn sàng với GPLX còn hạn.");
            var occupied = await _context.Set<Domain.Entities.Vehicle>().AsNoTracking()
                .AnyAsync(v => v.Id != id && v.DriverId == dto.DriverId, cancellationToken);
            if (occupied) throw new ConflictException("Tài xế đã được gán cho xe khác. Hãy gỡ phân công cũ trước.");
        }
        entity.DriverId = dto.DriverId;
        
        await _repository.UpdateAsync(entity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task PublishStatusAsync(Guid vehicleId, string status, string? label, CancellationToken cancellationToken)
    {
        try
        {
            await _messageBroker.PublishAsync("dispatcher.status.updated", new DispatcherStatusUpdatedEvent(
                Guid.NewGuid(), "vehicle", vehicleId.ToString(), status, DateTime.UtcNow,
                VehicleId: vehicleId, Label: label), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Vehicle status updated but dispatcher event could not be published for {VehicleId}", vehicleId);
        }
    }
}

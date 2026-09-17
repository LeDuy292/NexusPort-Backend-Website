using Microsoft.Extensions.Logging;
using NexusPort.Modules.Driver.Application.DTOs;
using NexusPort.Modules.Driver.Application.Interfaces;
using NexusPort.Infrastructure.ExternalServices;

namespace NexusPort.Modules.Driver.Application.Services;

public class DriverService : IDriverService
{
    private readonly IDriverRepository _repository;
    private readonly IMessageBrokerService _messageBroker;
    private readonly IS3StorageService _s3StorageService;
    private readonly ILogger<DriverService> _logger;

    public DriverService(
        IDriverRepository repository, 
        IMessageBrokerService messageBroker, 
        IS3StorageService s3StorageService,
        ILogger<DriverService> logger)
    {
        _repository = repository;
        _messageBroker = messageBroker;
        _s3StorageService = s3StorageService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DriverDto>> GetAllAsync(DriverFilterDto filter, CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(filter, cancellationToken);
        return entities.Select(e => new DriverDto
        {
            Id = e.Id,
            CarrierId = e.CarrierId,
            FullName = e.FullName,
            Phone = e.Phone,
            IdCardNumber = e.IdCardNumber,
            LicenseNumber = e.LicenseNumber,
            Status = e.Status.ToString(),
            PhotoUrl = _s3StorageService.GetPresignedUrl(e.PhotoUrl),
            IdCardFrontUrl = _s3StorageService.GetPresignedUrl(e.IdCardFrontUrl),
            LicenseImageUrl = _s3StorageService.GetPresignedUrl(e.LicenseImageUrl),
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<DriverDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new DriverDto
        {
            Id = entity.Id,
            CarrierId = entity.CarrierId,
            FullName = entity.FullName,
            Phone = entity.Phone,
            IdCardNumber = entity.IdCardNumber,
            LicenseNumber = entity.LicenseNumber,
            Status = entity.Status.ToString(),
            PhotoUrl = _s3StorageService.GetPresignedUrl(entity.PhotoUrl),
            IdCardFrontUrl = _s3StorageService.GetPresignedUrl(entity.IdCardFrontUrl),
            LicenseImageUrl = _s3StorageService.GetPresignedUrl(entity.LicenseImageUrl),
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<DriverDto> CreateAsync(Guid carrierId, CreateDriverDto dto, CancellationToken cancellationToken = default)
    {
        if (await _repository.ExistsByLicenseAsync(carrierId, dto.LicenseNumber, null, cancellationToken))
        {
            throw new InvalidOperationException("Driver with this license number already exists for this company.");
        }

        var entity = new NexusPort.Modules.Driver.Domain.Entities.Driver(
            carrierId: carrierId,
            fullName: dto.FullName,
            licenseNumber: dto.LicenseNumber,
            phone: dto.Phone,
            idCardNumber: dto.IdCardNumber,
            status: NexusPort.Modules.Driver.Domain.Enums.DriverStatus.active
        );
        entity.PhotoUrl = dto.PhotoUrl;
        entity.IdCardFrontUrl = dto.IdCardFrontUrl;
        entity.LicenseImageUrl = dto.LicenseImageUrl;

        await _repository.AddAsync(entity, cancellationToken);
        
        return new DriverDto
        {
            Id = entity.Id,
            CarrierId = entity.CarrierId,
            FullName = entity.FullName,
            Phone = entity.Phone,
            IdCardNumber = entity.IdCardNumber,
            LicenseNumber = entity.LicenseNumber,
            Status = entity.Status.ToString(),
            PhotoUrl = _s3StorageService.GetPresignedUrl(entity.PhotoUrl),
            IdCardFrontUrl = _s3StorageService.GetPresignedUrl(entity.IdCardFrontUrl),
            LicenseImageUrl = _s3StorageService.GetPresignedUrl(entity.LicenseImageUrl),
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<DriverDto> UpdateAsync(Guid id, UpdateDriverDto dto, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Driver not found.");

        entity.FullName = dto.FullName;
        entity.Phone = dto.Phone;
        entity.IdCardNumber = dto.IdCardNumber;
        if (dto.PhotoUrl != null)
        {
            entity.PhotoUrl = dto.PhotoUrl;
        }
        if (dto.IdCardFrontUrl != null)
        {
            entity.IdCardFrontUrl = dto.IdCardFrontUrl;
        }
        if (dto.LicenseImageUrl != null)
        {
            entity.LicenseImageUrl = dto.LicenseImageUrl;
        }

        await _repository.UpdateAsync(entity, cancellationToken);

        return new DriverDto
        {
            Id = entity.Id,
            CarrierId = entity.CarrierId,
            FullName = entity.FullName,
            Phone = entity.Phone,
            IdCardNumber = entity.IdCardNumber,
            LicenseNumber = entity.LicenseNumber,
            Status = entity.Status.ToString(),
            PhotoUrl = _s3StorageService.GetPresignedUrl(entity.PhotoUrl),
            IdCardFrontUrl = _s3StorageService.GetPresignedUrl(entity.IdCardFrontUrl),
            LicenseImageUrl = _s3StorageService.GetPresignedUrl(entity.LicenseImageUrl),
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task ToggleStatusAsync(Guid id, string status, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Driver not found.");

        if (Enum.TryParse<NexusPort.Modules.Driver.Domain.Enums.DriverStatus>(status, true, out var parsedStatus))
        {
            entity.Status = parsedStatus;
            await _repository.UpdateAsync(entity, cancellationToken);
            // If the driver is banned (or inactive), they should be kicked out of any vehicle they are assigned to
            if (parsedStatus == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.banned || 
                parsedStatus == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.inactive)
            {
                await _repository.UnassignVehiclesFromDriverAsync(id, cancellationToken);
            }
            
            await PublishStatusAsync(entity.Id, entity.Status.ToString(), entity.FullName, cancellationToken);
        }
        else
        {
            throw new ArgumentException("Invalid driver status.");
        }
    }

    private async Task PublishStatusAsync(Guid driverId, string status, string? label, CancellationToken cancellationToken)
    {
        try
        {
            await _messageBroker.PublishAsync("dispatcher.status.updated", new DispatcherStatusUpdatedEvent(
                Guid.NewGuid(), "driver", driverId.ToString(), status, DateTime.UtcNow,
                DriverId: driverId, Label: label), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Driver status updated but dispatcher event could not be published for {DriverId}", driverId);
        }
    }
}

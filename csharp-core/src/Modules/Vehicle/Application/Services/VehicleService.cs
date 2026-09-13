using NexusPort.Modules.Vehicle.Application.DTOs;
using NexusPort.Modules.Vehicle.Application.Interfaces;
using NexusPort.Infrastructure.ExternalServices;

namespace NexusPort.Modules.Vehicle.Application.Services;

public class VehicleService : IVehicleService
{
    private readonly IVehicleRepository _repository;
    private readonly IMessageBrokerService _messageBroker;
    private readonly ILogger<VehicleService> _logger;

    public VehicleService(IVehicleRepository repository, IMessageBrokerService messageBroker, ILogger<VehicleService> logger)
    {
        _repository = repository;
        _messageBroker = messageBroker;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VehicleDto>> GetAllAsync(VehicleFilterDto filter, CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(filter, cancellationToken);
        return entities.Select(e => new VehicleDto
        {
            Id = e.Id,
            CarrierId = e.CarrierId,
            DriverId = e.DriverId,
            PlateNumber = e.PlateNumber,
            Status = e.Status.ToString(),
            VehicleType = e.VehicleType,
            Description = e.Description,
            CreatedAt = e.CreatedAt
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
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Vehicle not found.");

        // Additional validation: Check if driver exists and belongs to the same CarrierId could be done here or in Controller.
        entity.DriverId = dto.DriverId;
        
        await _repository.UpdateAsync(entity, cancellationToken);
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

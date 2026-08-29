using NexusPort.Modules.Vehicle.Application.DTOs;
using NexusPort.Modules.Vehicle.Application.Interfaces;

namespace NexusPort.Modules.Vehicle.Application.Services;

public class VehicleService : IVehicleService
{
    private readonly IVehicleRepository _repository;

    public VehicleService(IVehicleRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<VehicleDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new VehicleDto
        {
            Id = e.Id,
            PlateNumber = e.PlateNumber,
            Status = e.Status,
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
            PlateNumber = entity.PlateNumber,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<VehicleDto> CreateAsync(CreateVehicleDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Vehicle.Domain.Entities.Vehicle
        {
            PlateNumber = dto.PlateNumber,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new VehicleDto
        {
            Id = entity.Id,
            PlateNumber = entity.PlateNumber,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

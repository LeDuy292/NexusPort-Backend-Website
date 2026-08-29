using NexusPort.Modules.Vessel.Application.DTOs;
using NexusPort.Modules.Vessel.Application.Interfaces;

namespace NexusPort.Modules.Vessel.Application.Services;

public class VesselService : IVesselService
{
    private readonly IVesselRepository _repository;

    public VesselService(IVesselRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<VesselDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new VesselDto
        {
            Id = e.Id,
            VesselName = e.VesselName,
            Status = e.Status,
            Description = e.Description,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<VesselDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new VesselDto
        {
            Id = entity.Id,
            VesselName = entity.VesselName,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<VesselDto> CreateAsync(CreateVesselDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Vessel.Domain.Entities.Vessel
        {
            VesselName = dto.VesselName,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new VesselDto
        {
            Id = entity.Id,
            VesselName = entity.VesselName,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

using NexusPort.Modules.Driver.Application.DTOs;
using NexusPort.Modules.Driver.Application.Interfaces;

namespace NexusPort.Modules.Driver.Application.Services;

public class DriverService : IDriverService
{
    private readonly IDriverRepository _repository;

    public DriverService(IDriverRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<DriverDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new DriverDto
        {
            Id = e.Id,
            FullName = e.FullName,
            Status = e.Status,
            Description = e.Description,
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
            FullName = entity.FullName,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<DriverDto> CreateAsync(CreateDriverDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Driver.Domain.Entities.Driver
        {
            FullName = dto.FullName,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new DriverDto
        {
            Id = entity.Id,
            FullName = entity.FullName,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

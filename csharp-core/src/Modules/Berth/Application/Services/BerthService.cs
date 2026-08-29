using NexusPort.Modules.Berth.Application.DTOs;
using NexusPort.Modules.Berth.Application.Interfaces;

namespace NexusPort.Modules.Berth.Application.Services;

public class BerthService : IBerthService
{
    private readonly IBerthRepository _repository;

    public BerthService(IBerthRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<BerthDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new BerthDto
        {
            Id = e.Id,
            BerthCode = e.BerthCode,
            Status = e.Status,
            Description = e.Description,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<BerthDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new BerthDto
        {
            Id = entity.Id,
            BerthCode = entity.BerthCode,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<BerthDto> CreateAsync(CreateBerthDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Berth.Domain.Entities.Berth
        {
            BerthCode = dto.BerthCode,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new BerthDto
        {
            Id = entity.Id,
            BerthCode = entity.BerthCode,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

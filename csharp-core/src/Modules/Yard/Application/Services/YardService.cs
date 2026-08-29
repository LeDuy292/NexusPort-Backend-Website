using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;

namespace NexusPort.Modules.Yard.Application.Services;

public class YardService : IYardService
{
    private readonly IYardRepository _repository;

    public YardService(IYardRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<YardBlockDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new YardBlockDto
        {
            Id = e.Id,
            BlockCode = e.BlockCode,
            Status = e.Status,
            Description = e.Description,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<YardBlockDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new YardBlockDto
        {
            Id = entity.Id,
            BlockCode = entity.BlockCode,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<YardBlockDto> CreateAsync(CreateYardBlockDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Yard.Domain.Entities.YardBlock
        {
            BlockCode = dto.BlockCode,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new YardBlockDto
        {
            Id = entity.Id,
            BlockCode = entity.BlockCode,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

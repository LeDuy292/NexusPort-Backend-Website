using NexusPort.Modules.Container.Application.DTOs;
using NexusPort.Modules.Container.Application.Interfaces;

namespace NexusPort.Modules.Container.Application.Services;

public class ContainerService : IContainerService
{
    private readonly IContainerRepository _repository;

    public ContainerService(IContainerRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<ContainerDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new ContainerDto
        {
            Id = e.Id,
            ContainerNumber = e.ContainerNumber,
            Status = e.Status,
            Description = e.Description,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new ContainerDto
        {
            Id = entity.Id,
            ContainerNumber = entity.ContainerNumber,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<ContainerDto> CreateAsync(CreateContainerDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Container.Domain.Entities.Container
        {
            ContainerNumber = dto.ContainerNumber,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new ContainerDto
        {
            Id = entity.Id,
            ContainerNumber = entity.ContainerNumber,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

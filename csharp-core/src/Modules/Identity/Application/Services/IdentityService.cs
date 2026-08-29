using NexusPort.Modules.Identity.Application.DTOs;
using NexusPort.Modules.Identity.Application.Interfaces;

namespace NexusPort.Modules.Identity.Application.Services;

public class IdentityService : IIdentityService
{
    private readonly IIdentityRepository _repository;

    public IdentityService(IIdentityRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new UserDto
        {
            Id = e.Id,
            Username = e.Username,
            Status = e.Status,
            Description = e.Description,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new UserDto
        {
            Id = entity.Id,
            Username = entity.Username,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<UserDto> CreateAsync(CreateUserDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Identity.Domain.Entities.User
        {
            Username = dto.Username,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new UserDto
        {
            Id = entity.Id,
            Username = entity.Username,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

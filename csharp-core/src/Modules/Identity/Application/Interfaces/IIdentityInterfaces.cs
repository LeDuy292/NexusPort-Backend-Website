namespace NexusPort.Modules.Identity.Application.Interfaces;

public interface IIdentityRepository
{
    Task<NexusPort.Modules.Identity.Domain.Entities.User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Identity.Domain.Entities.User>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Identity.Domain.Entities.User entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Identity.Domain.Entities.User entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IIdentityService
{
    Task<IReadOnlyList<DTOs.UserDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.UserDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.UserDto> CreateAsync(DTOs.CreateUserDto dto, CancellationToken cancellationToken = default);
}

namespace NexusPort.Modules.Container.Application.Interfaces;

public interface IContainerRepository
{
    Task<NexusPort.Modules.Container.Domain.Entities.Container?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Container.Domain.Entities.Container>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Container.Domain.Entities.Container entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Container.Domain.Entities.Container entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IContainerService
{
    Task<IReadOnlyList<DTOs.ContainerDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.ContainerDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.ContainerDto> CreateAsync(DTOs.CreateContainerDto dto, CancellationToken cancellationToken = default);
}

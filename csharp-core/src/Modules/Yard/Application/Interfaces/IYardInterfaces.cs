namespace NexusPort.Modules.Yard.Application.Interfaces;

public interface IYardRepository
{
    Task<NexusPort.Modules.Yard.Domain.Entities.YardBlock?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Yard.Domain.Entities.YardBlock>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Yard.Domain.Entities.YardBlock entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Yard.Domain.Entities.YardBlock entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IYardService
{
    Task<IReadOnlyList<DTOs.YardBlockDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.YardBlockDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.YardBlockDto> CreateAsync(DTOs.CreateYardBlockDto dto, CancellationToken cancellationToken = default);
}

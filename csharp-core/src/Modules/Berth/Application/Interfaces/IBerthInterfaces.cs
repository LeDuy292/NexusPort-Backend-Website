namespace NexusPort.Modules.Berth.Application.Interfaces;

public interface IBerthRepository
{
    Task<NexusPort.Modules.Berth.Domain.Entities.Berth?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Berth.Domain.Entities.Berth>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Berth.Domain.Entities.Berth entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Berth.Domain.Entities.Berth entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IBerthService
{
    Task<IReadOnlyList<DTOs.BerthDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.BerthDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.BerthDto> CreateAsync(DTOs.CreateBerthDto dto, CancellationToken cancellationToken = default);
}

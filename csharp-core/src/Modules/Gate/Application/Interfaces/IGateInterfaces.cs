namespace NexusPort.Modules.Gate.Application.Interfaces;

public interface IGateRepository
{
    Task<NexusPort.Modules.Gate.Domain.Entities.GateTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Gate.Domain.Entities.GateTransaction>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Gate.Domain.Entities.GateTransaction entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Gate.Domain.Entities.GateTransaction entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IGateService
{
    Task<IReadOnlyList<DTOs.GateTransactionDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.GateTransactionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.GateTransactionDto> CreateAsync(DTOs.CreateGateTransactionDto dto, CancellationToken cancellationToken = default);
}

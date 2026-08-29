namespace NexusPort.Modules.Driver.Application.Interfaces;

public interface IDriverRepository
{
    Task<NexusPort.Modules.Driver.Domain.Entities.Driver?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Driver.Domain.Entities.Driver>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Driver.Domain.Entities.Driver entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Driver.Domain.Entities.Driver entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IDriverService
{
    Task<IReadOnlyList<DTOs.DriverDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.DriverDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.DriverDto> CreateAsync(DTOs.CreateDriverDto dto, CancellationToken cancellationToken = default);
}

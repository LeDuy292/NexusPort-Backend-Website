namespace NexusPort.Modules.Vessel.Application.Interfaces;

public interface IVesselRepository
{
    Task<NexusPort.Modules.Vessel.Domain.Entities.Vessel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Vessel.Domain.Entities.Vessel>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Vessel.Domain.Entities.Vessel entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Vessel.Domain.Entities.Vessel entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IVesselService
{
    Task<IReadOnlyList<DTOs.VesselDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.VesselDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.VesselDto> CreateAsync(DTOs.CreateVesselDto dto, CancellationToken cancellationToken = default);
}

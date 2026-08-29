namespace NexusPort.Modules.Vehicle.Application.Interfaces;

public interface IVehicleRepository
{
    Task<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Vehicle.Domain.Entities.Vehicle entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Vehicle.Domain.Entities.Vehicle entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IVehicleService
{
    Task<IReadOnlyList<DTOs.VehicleDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.VehicleDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.VehicleDto> CreateAsync(DTOs.CreateVehicleDto dto, CancellationToken cancellationToken = default);
}

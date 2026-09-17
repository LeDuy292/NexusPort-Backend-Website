namespace NexusPort.Modules.Vehicle.Application.Interfaces;

public interface IVehicleRepository
{
    Task<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>> GetAllAsync(DTOs.VehicleFilterDto filter, CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Vehicle.Domain.Entities.Vehicle entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Vehicle.Domain.Entities.Vehicle entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsByPlateNumberAsync(string plateNumber, Guid? excludeVehicleId = null, CancellationToken cancellationToken = default);
}

public interface IVehicleService
{
    Task<IReadOnlyList<DTOs.VehicleDto>> GetAllAsync(DTOs.VehicleFilterDto filter, CancellationToken cancellationToken = default);
    Task<DTOs.VehicleDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.VehicleDto> CreateAsync(Guid carrierId, DTOs.CreateVehicleDto dto, CancellationToken cancellationToken = default);
    Task<DTOs.VehicleDto> UpdateAsync(Guid id, DTOs.UpdateVehicleDto dto, CancellationToken cancellationToken = default);
    Task ToggleStatusAsync(Guid id, string status, CancellationToken cancellationToken = default);
    Task AssignDriverAsync(Guid id, DTOs.AssignDriverDto dto, CancellationToken cancellationToken = default);
}

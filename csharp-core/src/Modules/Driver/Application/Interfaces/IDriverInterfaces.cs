namespace NexusPort.Modules.Driver.Application.Interfaces;

public interface IDriverRepository
{
    Task<NexusPort.Modules.Driver.Domain.Entities.Driver?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Driver.Domain.Entities.Driver>> GetAllAsync(DTOs.DriverFilterDto filter, CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Driver.Domain.Entities.Driver entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Driver.Domain.Entities.Driver entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsByLicenseAsync(Guid carrierId, string licenseNumber, Guid? excludeDriverId = null, CancellationToken cancellationToken = default);
}

public interface IDriverService
{
    Task<IReadOnlyList<DTOs.DriverDto>> GetAllAsync(DTOs.DriverFilterDto filter, CancellationToken cancellationToken = default);
    Task<DTOs.DriverDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.DriverDto> CreateAsync(Guid carrierId, DTOs.CreateDriverDto dto, CancellationToken cancellationToken = default);
    Task<DTOs.DriverDto> UpdateAsync(Guid id, DTOs.UpdateDriverDto dto, CancellationToken cancellationToken = default);
    Task ToggleStatusAsync(Guid id, string status, CancellationToken cancellationToken = default);
}

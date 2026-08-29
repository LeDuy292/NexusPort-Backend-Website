namespace NexusPort.Modules.Equipment.Application.Interfaces;

public interface IEquipmentRepository
{
    Task<NexusPort.Modules.Equipment.Domain.Entities.Equipment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Equipment.Domain.Entities.Equipment>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Equipment.Domain.Entities.Equipment entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Equipment.Domain.Entities.Equipment entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IEquipmentService
{
    Task<IReadOnlyList<DTOs.EquipmentDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.EquipmentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.EquipmentDto> CreateAsync(DTOs.CreateEquipmentDto dto, CancellationToken cancellationToken = default);
}

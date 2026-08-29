namespace NexusPort.Modules.Dispatcher.Application.Interfaces;

public interface IDispatcherRepository
{
    Task<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IDispatcherService
{
    Task<IReadOnlyList<DTOs.WorkOrderDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.WorkOrderDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.WorkOrderDto> CreateAsync(DTOs.CreateWorkOrderDto dto, CancellationToken cancellationToken = default);
}

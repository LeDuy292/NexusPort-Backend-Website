using NexusPort.Modules.Dispatcher.Application.DTOs;
using NexusPort.Modules.Dispatcher.Application.Interfaces;

namespace NexusPort.Modules.Dispatcher.Application.Services;

public class DispatcherService : IDispatcherService
{
    private readonly IDispatcherRepository _repository;

    public DispatcherService(IDispatcherRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<WorkOrderDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new WorkOrderDto
        {
            Id = e.Id,
            OrderNumber = e.OrderNumber,
            Status = e.Status,
            Description = e.Description,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<WorkOrderDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new WorkOrderDto
        {
            Id = entity.Id,
            OrderNumber = entity.OrderNumber,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<WorkOrderDto> CreateAsync(CreateWorkOrderDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder
        {
            OrderNumber = dto.OrderNumber,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new WorkOrderDto
        {
            Id = entity.Id,
            OrderNumber = entity.OrderNumber,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

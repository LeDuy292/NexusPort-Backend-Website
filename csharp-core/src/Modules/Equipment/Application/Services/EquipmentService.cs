using NexusPort.Modules.Equipment.Application.DTOs;
using NexusPort.Modules.Equipment.Application.Interfaces;
using NexusPort.Infrastructure.ExternalServices;

namespace NexusPort.Modules.Equipment.Application.Services;

public class EquipmentService : IEquipmentService
{
    private readonly IEquipmentRepository _repository;
    private readonly IMessageBrokerService _messageBroker;
    private readonly ILogger<EquipmentService> _logger;

    public EquipmentService(IEquipmentRepository repository, IMessageBrokerService messageBroker, ILogger<EquipmentService> logger)
    {
        _repository = repository;
        _messageBroker = messageBroker;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EquipmentDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new EquipmentDto
        {
            Id = e.Id,
            EquipmentCode = e.EquipmentCode,
            Status = e.Status,
            Description = e.Description,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<EquipmentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new EquipmentDto
        {
            Id = entity.Id,
            EquipmentCode = entity.EquipmentCode,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<EquipmentDto> CreateAsync(CreateEquipmentDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Equipment.Domain.Entities.Equipment
        {
            EquipmentCode = dto.EquipmentCode,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        await PublishStatusAsync(entity.Id, entity.Status, entity.EquipmentCode, cancellationToken);
        return new EquipmentDto
        {
            Id = entity.Id,
            EquipmentCode = entity.EquipmentCode,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    private async Task PublishStatusAsync(Guid equipmentId, string status, string? label, CancellationToken cancellationToken)
    {
        try
        {
            await _messageBroker.PublishAsync("dispatcher.status.updated", new DispatcherStatusUpdatedEvent(
                Guid.NewGuid(), "equipment", equipmentId.ToString(), status, DateTime.UtcNow,
                Label: label), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Equipment status saved but dispatcher event could not be published for {EquipmentId}", equipmentId);
        }
    }
}

using NexusPort.Modules.Equipment.Application.DTOs;
using NexusPort.Modules.Equipment.Application.Interfaces;

namespace NexusPort.Modules.Equipment.Application.Services;

public class EquipmentService : IEquipmentService
{
    private readonly IEquipmentRepository _repository;

    public EquipmentService(IEquipmentRepository repository)
    {
        _repository = repository;
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
        return new EquipmentDto
        {
            Id = entity.Id,
            EquipmentCode = entity.EquipmentCode,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

using NexusPort.Modules.Equipment.Application.DTOs;
using NexusPort.Modules.Equipment.Application.Interfaces;
using NexusPort.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging;

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
        await EnsureSeedEquipmentsAsync(cancellationToken);
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(MapToDto).ToList();
    }

    public async Task<IReadOnlyList<EquipmentDto>> GetAvailableAsync(string? blockCode, CancellationToken cancellationToken = default)
    {
        await EnsureSeedEquipmentsAsync(cancellationToken);
        var entities = await _repository.GetAvailableByBlockAsync(blockCode, cancellationToken);
        return entities.Select(MapToDto).ToList();
    }

    public async Task<EquipmentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return MapToDto(entity);
    }

    public async Task<EquipmentDto> CreateAsync(CreateEquipmentDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Equipment.Domain.Entities.Equipment
        {
            EquipmentCode = dto.EquipmentCode,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.EquipmentCode : dto.Name,
            EquipmentType = dto.EquipmentType,
            BlockCode = dto.BlockCode,
            Description = dto.Description,
            Status = string.IsNullOrWhiteSpace(dto.Status) ? "available" : dto.Status
        };
        await _repository.AddAsync(entity, cancellationToken);
        await PublishStatusAsync(entity.Id, entity.Status, entity.EquipmentCode, cancellationToken);
        return MapToDto(entity);
    }

    private static EquipmentDto MapToDto(NexusPort.Modules.Equipment.Domain.Entities.Equipment entity)
    {
        return new EquipmentDto
        {
            Id = entity.Id,
            EquipmentCode = entity.EquipmentCode,
            Name = entity.Name,
            EquipmentType = entity.EquipmentType,
            Status = entity.Status,
            BlockCode = entity.BlockCode,
            OperatorId = entity.OperatorId,
            OperatorName = entity.OperatorName,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    private async Task EnsureSeedEquipmentsAsync(CancellationToken cancellationToken)
    {
        var existing = await _repository.GetAllAsync(cancellationToken);
        if (existing.Count == 0)
        {
            var seedList = new List<NexusPort.Modules.Equipment.Domain.Entities.Equipment>
            {
                new("RTG-01", "Cẩu bãi RTG 01 (Khu A)", "RTG", "A01", "available", "Cẩu bánh lốp RTG chuyên dụng Block A"),
                new("RTG-02", "Cẩu bãi RTG 02 (Khu A)", "RTG", "A01", "available", "Cẩu bánh lốp RTG 40T Block A"),
                new("RTG-03", "Cẩu bãi RTG 03 (Khu B)", "RTG", "B02", "available", "Cẩu RTG Block B bãi hàng khô"),
                new("RTG-04", "Cẩu bãi RTG 04 (Khu B)", "RTG", "B02", "busy", "Đang cẩu Task khác tại Block B"),
                new("RS-01", "Xe nâng Reach Stacker 01 (Khu A)", "ReachStacker", "A01", "available", "Xe nâng gắp container 45T Block A"),
                new("RS-02", "Xe nâng Reach Stacker 02 (Khu C)", "ReachStacker", "C01", "maintenance", "Đang bảo trì định kỳ"),
                new("QC-01", "Cẩu bờ STS QC 01 (Cầu B-01)", "QC", "A01", "available", "Cẩu giàn bờ Panamax"),
                new("RTG-05", "Cẩu bãi RTG 05 (Khu D)", "RTG", "D01", "available", "Cẩu RTG Block D"),
            };

            foreach (var item in seedList)
            {
                await _repository.AddAsync(item, cancellationToken);
            }
        }
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

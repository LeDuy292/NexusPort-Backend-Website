using NexusPort.Modules.Gate.Application.DTOs;
using NexusPort.Modules.Gate.Application.Interfaces;

namespace NexusPort.Modules.Gate.Application.Services;

public class GateService : IGateService
{
    private readonly IGateRepository _repository;

    public GateService(IGateRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<GateTransactionDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new GateTransactionDto
        {
            Id = e.Id,
            TransactionCode = e.TransactionCode,
            Status = e.Status,
            Description = e.Description,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<GateTransactionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new GateTransactionDto
        {
            Id = entity.Id,
            TransactionCode = entity.TransactionCode,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<GateTransactionDto> CreateAsync(CreateGateTransactionDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Gate.Domain.Entities.GateTransaction
        {
            TransactionCode = dto.TransactionCode,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new GateTransactionDto
        {
            Id = entity.Id,
            TransactionCode = entity.TransactionCode,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

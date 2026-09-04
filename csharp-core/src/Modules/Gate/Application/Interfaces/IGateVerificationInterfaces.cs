using NexusPort.Modules.Gate.Application.DTOs;
using NexusPort.Modules.Gate.Domain.Entities;

namespace NexusPort.Modules.Gate.Application.Interfaces;

public interface IGateVerificationRepository
{
    Task<GateVerificationRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GateVerificationRecord?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GateVerificationRecord>> GetListAsync(GateVerificationFilterDto filter, CancellationToken cancellationToken = default);
    Task<int> CountAsync(GateVerificationFilterDto filter, CancellationToken cancellationToken = default);
    Task AddAsync(GateVerificationRecord entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(GateVerificationRecord entity, CancellationToken cancellationToken = default);
}

public interface IGateVerificationService
{
    Task<GateVerificationResultDto> VerifyGateScanAsync(GateRecognitionEventDto request, CancellationToken cancellationToken = default);
    Task<GateVerificationRecordDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GateVerificationRecordDto>> GetListAsync(GateVerificationFilterDto filter, CancellationToken cancellationToken = default);
    Task<GateVerificationRecordDto?> ManualOverrideAsync(Guid id, ManualOverrideDto dto, CancellationToken cancellationToken = default);
    Task<Domain.Rules.GateRuleEvaluationResult> EvaluateRulesAsync(GateRulePreCheckRequestDto request, CancellationToken cancellationToken = default);
}

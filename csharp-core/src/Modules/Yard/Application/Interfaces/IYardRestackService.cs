using NexusPort.Modules.Yard.Application.DTOs;

namespace NexusPort.Modules.Yard.Application.Interfaces;

public interface IYardRestackService
{
    Task<AnalyzeBlockedContainerResponseDto> AnalyzeBlockedContainerAsync(AnalyzeBlockedContainerRequestDto dto, CancellationToken cancellationToken = default);
    Task<RestackPlanDto> CreatePlanAsync(CreateRestackPlanDto dto, string createdBy, CancellationToken cancellationToken = default);
    Task<RestackPlanDto?> GetPlanByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RestackPlanDto>> GetAllPlansAsync(string? status, CancellationToken cancellationToken = default);
    Task<DeployRestackPlanResponseDto> DeployPlanAsync(Guid planId, string deployedBy, CancellationToken cancellationToken = default);
    Task<RestackPlanDto> CompleteStepAsync(Guid planId, int stepNumber, AdvanceRestackStepDto dto, string operatorName, CancellationToken cancellationToken = default);
    Task<RestackPlanDto> CancelPlanAsync(Guid planId, string reason, CancellationToken cancellationToken = default);
    Task EnsureSeedPlansAsync(CancellationToken cancellationToken = default);
}

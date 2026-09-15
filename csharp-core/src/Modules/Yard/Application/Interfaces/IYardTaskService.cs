using NexusPort.Modules.Yard.Application.DTOs;

namespace NexusPort.Modules.Yard.Application.Interfaces;

public interface IYardTaskService
{
    Task<IReadOnlyList<YardTaskDto>> GetAllAsync(string? blockCode, string? status, CancellationToken cancellationToken = default);
    Task<YardTaskDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<YardTaskDto> AssignEquipmentAsync(Guid taskId, AssignEquipmentRequestDto dto, string assignedBy, CancellationToken cancellationToken = default);
    Task<YardTaskDto> StartLiftAsync(Guid taskId, StartLiftDto dto, string operatorName, CancellationToken cancellationToken = default);
    Task<YardTaskDto> CompleteLiftAsync(Guid taskId, CompleteLiftDto dto, string operatorName, CancellationToken cancellationToken = default);
    Task<YardTaskDto> ReceiveContainerAtYardAsync(YardReceivingInspectionDto dto, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<YardOperatorDto>> GetAvailableOperatorsAsync(CancellationToken cancellationToken = default);
    Task<YardTaskDto> CreateTaskAsync(CreateYardTaskDto dto, CancellationToken cancellationToken = default);
    Task EnsureSeedTasksAsync(CancellationToken cancellationToken = default);
}

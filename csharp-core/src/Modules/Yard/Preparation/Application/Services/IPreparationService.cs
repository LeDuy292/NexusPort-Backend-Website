using NexusPort.Modules.Yard.Preparation.Application.DTOs;
using NexusPort.Modules.Yard.Preparation.Domain;

namespace NexusPort.Modules.Yard.Preparation.Application.Services;

public interface IPreparationService
{
    // Idempotent triggers
    Task<PreparationTaskDto?> CheckAndTriggerPreparationAsync(Guid bookingId, string containerNo, Guid? serviceOrderId = null, CancellationToken cancellationToken = default);
    Task HandleSlotRescheduledAsync(Guid bookingId, DateTimeOffset newAppointmentStart, CancellationToken cancellationToken = default);
    Task HandleSlotCancelledAsync(Guid bookingId, string reason, CancellationToken cancellationToken = default);
    Task HandleEDoHeldAsync(string containerNo, HoldFlag holdFlag, CancellationToken cancellationToken = default);
    Task HandleEDoExpiredAsync(string containerNo, CancellationToken cancellationToken = default);

    // Preparation Tasks (Dispatcher)
    Task<PagedResult<PreparationTaskDto>> GetTasksAsync(PreparationTaskStatus? status, DateTimeOffset? targetDate, string? containerNo, string? blockCode, PreparationDirection? direction, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<PreparationTaskDetailDto?> GetTaskByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PreparationTaskDto> ReplanTaskAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PreparationTaskDto> CancelTaskAsync(Guid id, CancelTaskRequestDto dto, CancellationToken cancellationToken = default);
    Task<PreparationTaskDto> ConfirmReadyAsync(Guid id, ConfirmReadyRequestDto dto, CancellationToken cancellationToken = default);
    Task<PlanBatchResultDto> PlanBatchAsync(PlanBatchRequestDto dto, CancellationToken cancellationToken = default);
    Task<PreparationDashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<PreparationConfigDto> GetConfigAsync(CancellationToken cancellationToken = default);
    Task<PreparationConfigDto> UpdateConfigAsync(PreparationConfigDto dto, CancellationToken cancellationToken = default);

    // Outbound Preparation (Vessel Loading)
    Task<PreparationTaskDto> CreateOutboundPreparationTaskAsync(CreateOutboundPreparationTaskRequestDto dto, CancellationToken cancellationToken = default);
    Task<OutboundEligibilityCheckResultDto> ValidateOutboundEligibilityAsync(string containerNo, string? voyageCode = null, CancellationToken cancellationToken = default);

    // Reefer dwell timer handling
    Task<PreparationTaskDto> HandleReeferUnplugAsync(Guid id, int graceMinutes = 45, CancellationToken cancellationToken = default);
    Task<PreparationTaskDto> HandleReeferReplugAsync(Guid id, CancellationToken cancellationToken = default);

    // Work Orders (YardStaff / Dispatcher)
    Task<PagedResult<WorkOrderDto>> GetWorkOrdersAsync(WorkOrderStatus? status, Guid? assigneeId, Guid? equipmentId, WorkOrderType? type, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<WorkOrderDto> AssignWorkOrderAsync(Guid id, AssignWorkOrderRequestDto dto, CancellationToken cancellationToken = default);
    Task<WorkOrderDto> StartWorkOrderAsync(Guid id, CancellationToken cancellationToken = default);
    Task<WorkOrderDto> CompleteWorkOrderAsync(Guid id, CompleteWorkOrderRequestDto dto, CancellationToken cancellationToken = default);
    Task<WorkOrderDto> FailWorkOrderAsync(Guid id, FailWorkOrderRequestDto dto, CancellationToken cancellationToken = default);

    // Stack inspection
    Task<List<StackSlotDto>> GetStackContainersAsync(Guid slotId, CancellationToken cancellationToken = default);
}

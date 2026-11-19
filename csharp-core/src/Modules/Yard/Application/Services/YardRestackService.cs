using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;
using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Yard.Application.Services;

public class YardRestackService : IYardRestackService
{
    private readonly AppDbContext _context;
    private readonly ILogger<YardRestackService> _logger;
    private static bool _tableEnsured = false;

    public YardRestackService(
        AppDbContext context,
        ILogger<YardRestackService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task<AnalyzeBlockedContainerResponseDto> AnalyzeBlockedContainerAsync(
        AnalyzeBlockedContainerRequestDto dto, CancellationToken cancellationToken = default)
        => LiveRestackAnalyzer.AnalyzeAsync(_context, dto, cancellationToken);

    public async Task<IReadOnlyList<RestackRiskDto>> GetUpcomingRisksAsync(CancellationToken cancellationToken = default)
    {
        var appointments = await _context.Database.SqlQueryRaw<RestackAppointmentRow>(@"
SELECT c.container_no AS ""ContainerNo"", MIN(b.appointment_start) AS ""AppointmentAt""
FROM bookings b
JOIN booking_containers bc ON bc.booking_id = b.id
JOIN containers c ON c.id = bc.container_id
WHERE lower(b.status::text) IN ('approved', 'ready', 'checkedin')
  AND lower(b.booking_type::text) = 'pickup'
  AND b.appointment_start BETWEEN now() - interval '24 hours' AND now() + interval '24 hours'
GROUP BY c.container_no
ORDER BY MIN(b.appointment_start)
LIMIT 100").ToListAsync(cancellationToken);

        var positions = await _context.Set<ContainerPosition>().AsNoTracking()
            .Include(p => p.Slot).ThenInclude(s => s.Block)
            .Where(p => p.IsCurrent).ToListAsync(cancellationToken);
        var ids = positions.Select(p => p.ContainerId).Distinct().ToArray();
        var containers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(cancellationToken);
        var positionById = positions.ToDictionary(p => p.ContainerId);
        var result = new List<RestackRiskDto>();
        foreach (var appointment in appointments)
        {
            var container = containers.FirstOrDefault(c => string.Equals(c.ContainerNumber,
                appointment.ContainerNo, StringComparison.OrdinalIgnoreCase));
            if (container == null || !positionById.TryGetValue(container.Id, out var position)) continue;
            var slot = position.Slot;
            var blockingCount = positions.Count(p => p.Slot.YardBlockId == slot.YardBlockId &&
                p.Slot.Bay == slot.Bay && p.Slot.Row == slot.Row && p.Slot.Tier > slot.Tier);
            if (blockingCount == 0) continue;
            result.Add(new RestackRiskDto
            {
                ContainerNo = appointment.ContainerNo,
                Location = LiveRestackAnalyzer.Location(slot),
                AppointmentAt = appointment.AppointmentAt,
                BlockingCount = blockingCount
            });
        }
        return result.OrderBy(r => r.AppointmentAt).ToList();
    }

    private sealed class RestackAppointmentRow
    {
        public string ContainerNo { get; set; } = string.Empty;
        public DateTime AppointmentAt { get; set; }
    }

    public async Task<RestackPlanDto> CreatePlanAsync(CreateRestackPlanDto dto, string createdBy, CancellationToken cancellationToken = default)
    {
        await EnsureTablesAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(dto.TargetContainerNo))
        {
            throw new ValidationException("TargetContainerNo", "Mã Container mục tiêu không được để trống.");
        }

        // Never trust client-generated steps or a previously analyzed slot snapshot.
        var current = await AnalyzeBlockedContainerAsync(new AnalyzeBlockedContainerRequestDto
        {
            ContainerNo = dto.TargetContainerNo,
            Location = dto.TargetLocation,
            TargetDestination = dto.TargetDestination,
            RestackBackToOriginal = dto.RestackBackToOriginal
        }, cancellationToken);
        if (!current.CanExecute)
            throw new ValidationException("BufferSlots", current.ValidationMessage);
        dto.Steps = null;
        var activePlan = await _context.Set<YardRestackPlan>().AnyAsync(p =>
            p.TargetContainerNo == dto.TargetContainerNo.Trim().ToUpper() &&
            (p.Status == "Draft" || p.Status == "In_Progress"), cancellationToken);
        if (activePlan)
            throw new ValidationException("Plan", "Container đã có kế hoạch đảo chuyển chưa kết thúc.");

        var planCode = $"RST-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..25];

        var plan = new YardRestackPlan(
            planCode: planCode,
            targetContainerNo: dto.TargetContainerNo.Trim().ToUpperInvariant(),
            targetLocation: dto.TargetLocation.Trim(),
            blockCode: string.IsNullOrWhiteSpace(dto.BlockCode) ? "C01" : dto.BlockCode.Trim(),
            targetDestination: string.IsNullOrWhiteSpace(dto.TargetDestination) ? "Xe Đầu Kéo Cổng C" : dto.TargetDestination.Trim(),
            restackBackToOriginal: dto.RestackBackToOriginal,
            assignedEquipmentCode: null,
            assignedOperatorName: null,
            createdBy: string.IsNullOrWhiteSpace(createdBy) ? "Yard Staff" : createdBy,
            notes: dto.Notes
        )
        {
            IsBillable = dto.IsBillable
        };

        // Nếu người dùng cung cấp danh sách bước tùy chỉnh
        if (dto.Steps != null && dto.Steps.Count > 0)
        {
            foreach (var stepInput in dto.Steps)
            {
                if (string.IsNullOrWhiteSpace(stepInput.ToLocation) || 
                    stepInput.ToLocation.Contains("KHÔNG CÓ SLOT", StringComparison.OrdinalIgnoreCase) ||
                    stepInput.ToLocation.Contains("NO_SLOT", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ValidationException("BufferSlot", $"Không thể tạo kế hoạch: Bước {stepInput.StepNumber} cho container {stepInput.ContainerNo} không có slot đệm hợp lệ!");
                }

                var step = new YardRestackPlanStep(
                    planId: plan.Id,
                    stepNumber: stepInput.StepNumber,
                    containerNo: stepInput.ContainerNo,
                    fromLocation: stepInput.FromLocation,
                    toLocation: stepInput.ToLocation,
                    isTargetContainer: stepInput.IsTargetContainer,
                    stepType: stepInput.StepType,
                    containerType: stepInput.ContainerType ?? "40FT HC",
                    equipmentCode: stepInput.EquipmentCode ?? plan.AssignedEquipmentCode,
                    operatorName: stepInput.OperatorName ?? plan.AssignedOperatorName,
                    estimatedDurationMinutes: 2,
                    fee: 0,
                    reason: stepInput.Reason
                );
                plan.Steps.Add(step);
            }
        }
        else
        {
            // Tự động phân tích và sinh bước mặc định
            var analysis = await AnalyzeBlockedContainerAsync(new AnalyzeBlockedContainerRequestDto
            {
                ContainerNo = dto.TargetContainerNo,
                Location = dto.TargetLocation,
                BlockCode = dto.BlockCode,
                TargetDestination = dto.TargetDestination,
                RestackBackToOriginal = dto.RestackBackToOriginal
            }, cancellationToken);

            if (!analysis.CanExecute)
            {
                throw new ValidationException("BufferSlots", $"Không thể tạo kế hoạch: {analysis.ValidationMessage}");
            }

            int stepOrder = 1;
            // 1. Các bước dời cont chặn sang slot đệm
            foreach (var blk in analysis.BlockingContainers)
            {
                plan.Steps.Add(new YardRestackPlanStep(
                    planId: plan.Id,
                    stepNumber: stepOrder++,
                    containerNo: blk.ContainerNo,
                    fromLocation: blk.CurrentLocation,
                    toLocation: blk.RecommendedBufferSlot,
                    isTargetContainer: false,
                    stepType: "MoveToBuffer",
                    containerType: blk.ContainerType,
                    equipmentCode: plan.AssignedEquipmentCode,
                    operatorName: plan.AssignedOperatorName,
                    estimatedDurationMinutes: 2,
                    fee: 250000m,
                    reason: blk.Reason
                ));
            }

            // 2. Bước giải phóng container mục tiêu
            plan.Steps.Add(new YardRestackPlanStep(
                planId: plan.Id,
                stepNumber: stepOrder++,
                containerNo: analysis.TargetContainer.ContainerNo,
                fromLocation: analysis.TargetContainer.CurrentLocation,
                toLocation: analysis.TargetContainer.TargetDestination,
                isTargetContainer: true,
                stepType: "ReleaseTarget",
                containerType: analysis.TargetContainer.ContainerType,
                equipmentCode: plan.AssignedEquipmentCode,
                operatorName: plan.AssignedOperatorName,
                estimatedDurationMinutes: 3,
                fee: 0,
                reason: $"Giải phóng container mục tiêu bàn giao {analysis.TargetContainer.TargetDestination}"
            ));

            // 3. Nếu có Restack Back: đưa các container đệm trở lại vị trí cột ban đầu
            if (dto.RestackBackToOriginal)
            {
                for (int i = analysis.BlockingContainers.Count - 1; i >= 0; i--)
                {
                    var blk = analysis.BlockingContainers[i];
                    plan.Steps.Add(new YardRestackPlanStep(
                        planId: plan.Id,
                        stepNumber: stepOrder++,
                        containerNo: blk.ContainerNo,
                        fromLocation: blk.RecommendedBufferSlot,
                        toLocation: blk.CurrentLocation,
                        isTargetContainer: false,
                        stepType: "RestackBack",
                        containerType: blk.ContainerType,
                        equipmentCode: plan.AssignedEquipmentCode,
                        operatorName: plan.AssignedOperatorName,
                        estimatedDurationMinutes: 2,
                        fee: 0,
                        reason: $"Khôi phục container {blk.ContainerNo} về lại vị trí cột ban đầu"
                    ));
                }
            }
        }

        plan.TotalMoves = plan.Steps.Count;
        plan.EstimatedMinutes = plan.Steps.Count * 2;
        plan.EstimatedFee = plan.Steps.Sum(s => s.Fee);

        await _context.Set<YardRestackPlan>().AddAsync(plan, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(plan);
    }

    public async Task<RestackPlanDto?> GetPlanByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureTablesAsync(cancellationToken);

        var plan = await _context.Set<YardRestackPlan>()
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        return plan == null ? null : MapToDto(plan);
    }

    public async Task<IReadOnlyList<RestackPlanDto>> GetAllPlansAsync(string? status, CancellationToken cancellationToken = default)
    {
        await EnsureTablesAsync(cancellationToken);

        var query = _context.Set<YardRestackPlan>()
            .Include(p => p.Steps)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(p => p.Status.ToLower() == status.ToLower().Trim());
        }

        var plans = await query.OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken);
        return plans.Select(MapToDto).ToList();
    }

    public async Task<DeployRestackPlanResponseDto> DeployPlanAsync(Guid planId, string deployedBy, CancellationToken cancellationToken = default)
    {
        await EnsureTablesAsync(cancellationToken);

        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var plan = await _context.Set<YardRestackPlan>()
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

        if (plan == null)
        {
            throw new NotFoundException($"Không tìm thấy kế hoạch đảo bãi với ID: {planId}");
        }

        if (plan.Status != "Draft")
            throw new ValidationException("Status", "Kế hoạch đã được phát hoặc không còn hợp lệ.");

        var analysis = await AnalyzeBlockedContainerAsync(new AnalyzeBlockedContainerRequestDto
        {
            ContainerNo = plan.TargetContainerNo,
            Location = plan.TargetLocation,
            TargetDestination = plan.TargetDestination,
            RestackBackToOriginal = plan.RestackBackToOriginal
        }, cancellationToken);
        if (!analysis.CanExecute)
            throw new ValidationException("BufferSlots", analysis.ValidationMessage);
        var moveSteps = plan.Steps.Where(s => s.StepType == "MoveToBuffer").OrderBy(s => s.StepNumber).ToList();
        var orderedSteps = plan.Steps.OrderBy(s => s.StepNumber).ToList();
        var targetSteps = orderedSteps.Where(s => s.IsTargetContainer).ToList();
        var targetStep = targetSteps.FirstOrDefault();
        if (targetSteps.Count != 1 || targetStep == null || targetStep.StepType != "ReleaseTarget" ||
            targetStep.StepNumber != moveSteps.Count + 1 ||
            !string.Equals(targetStep.ContainerNo, plan.TargetContainerNo, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(targetStep.FromLocation, plan.TargetLocation, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(targetStep.ToLocation, plan.TargetDestination, StringComparison.OrdinalIgnoreCase) ||
            orderedSteps.Select(s => s.StepNumber).Distinct().Count() != orderedSteps.Count)
            throw new ValidationException("Plan", "Chuỗi lệnh lấy container không hợp lệ; cần tạo kế hoạch mới.");
        if (moveSteps.Count != analysis.BlockingContainers.Count ||
            moveSteps.Where((step, index) =>
                !string.Equals(step.ContainerNo, analysis.BlockingContainers[index].ContainerNo, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(step.FromLocation, analysis.BlockingContainers[index].CurrentLocation, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(step.ToLocation, analysis.BlockingContainers[index].RecommendedBufferSlot, StringComparison.OrdinalIgnoreCase)).Any())
            throw new ValidationException("Plan", "Vị trí bãi đã thay đổi. Hãy phân tích và tạo lại kế hoạch.");

        var equipment = await _context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>()
            .Where(e => e.Status == "available" && (e.EquipmentType == "RTG" || e.EquipmentType == "ReachStacker"))
            .OrderByDescending(e => e.BlockCode == plan.BlockCode)
            .FirstOrDefaultAsync(cancellationToken);
        if (equipment == null)
            throw new ValidationException("Equipment", "Chưa có thiết bị cẩu sẵn sàng. Kế hoạch vẫn ở trạng thái nháp.");
        plan.AssignedEquipmentCode = equipment.EquipmentCode;
        plan.AssignedOperatorName = equipment.OperatorName;
        equipment.Status = "working";
        foreach (var step in plan.Steps)
        {
            step.EquipmentCode = equipment.EquipmentCode;
            step.OperatorName = equipment.OperatorName;
        }

        var slots = await _context.Set<YardSlot>().Include(s => s.Block).ToListAsync(cancellationToken);
        foreach (var move in moveSteps)
        {
            var slot = slots.FirstOrDefault(s =>
                string.Equals(LiveRestackAnalyzer.Location(s), move.ToLocation, StringComparison.OrdinalIgnoreCase));
            if (slot == null)
                throw new ValidationException("BufferSlot", $"Không tìm thấy ô {move.ToLocation}.");
            var reserved = await _context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE yard_slots SET status = 'reserved'::yard_slot_status WHERE id = {slot.Id} AND status = 'empty'::yard_slot_status", cancellationToken);
            if (reserved != 1)
                throw new ValidationException("BufferSlot", $"Ô {move.ToLocation} vừa được sử dụng. Hãy lập lại phương án.");
        }

        plan.Status = "In_Progress";
        var upcoming = await GetUpcomingRisksAsync(cancellationToken);
        var appointment = upcoming.FirstOrDefault(r => string.Equals(r.ContainerNo,
            plan.TargetContainerNo, StringComparison.OrdinalIgnoreCase));
        var releaseDueAt = appointment?.AppointmentAt ?? DateTime.UtcNow.AddHours(1);

        var generatedTaskCodes = new List<string>();

        // Duyệt từng bước để sinh YardTask tương ứng
        foreach (var step in plan.Steps.OrderBy(s => s.StepNumber))
        {
            var taskCode = $"MOV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..25];
            generatedTaskCodes.Add(taskCode);

            bool isFirstStep = step.StepNumber == 1;
            step.Status = isFirstStep ? "In_Progress" : "Assigned";

            var yardTask = new YardTask(
                taskCode: taskCode,
                containerNo: step.ContainerNo,
                blockCode: plan.BlockCode,
                operationType: "Relocate",
                fromLocation: step.FromLocation,
                toLocation: step.ToLocation,
                priority: step.IsTargetContainer ? "Critical" : "High",
                status: isFirstStep ? "In_Progress" : "Assigned",
                containerType: step.ContainerType ?? "40FT HC",
                cargoType: "Hàng Đảo Chuyển BRP",
                vehiclePlate: null,
                driverName: null,
                dueTime: releaseDueAt.AddMinutes(-(plan.Steps.Count - step.StepNumber) * 3),
                notes: $"NXP-126 Bước {step.StepNumber}: {step.Reason}",
                internalFee: step.Fee,
                shiftingReason: "Đảo container giải phóng tầng"
            )
            {
                EquipmentId = equipment.Id,
                EquipmentCode = step.EquipmentCode ?? plan.AssignedEquipmentCode,
                EquipmentType = equipment.EquipmentType,
                OperatorName = step.OperatorName ?? plan.AssignedOperatorName,
                AssignedBy = deployedBy,
                AssignedAt = DateTime.UtcNow,
                StartTime = isFirstStep ? DateTime.UtcNow : null
            };

            await _context.Set<YardTask>().AddAsync(yardTask, cancellationToken);
            step.YardTaskId = yardTask.Id;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DeployRestackPlanResponseDto
        {
            Success = true,
            Message = $"Triển khai kế hoạch {plan.PlanCode} thành công! Đã phát {generatedTaskCodes.Count} lệnh cẩu RTG.",
            Plan = MapToDto(plan),
            GeneratedTaskCodes = generatedTaskCodes
        };
    }

    public async Task<RestackPlanDto> CompleteStepAsync(Guid planId, int stepNumber, AdvanceRestackStepDto dto, string operatorName, CancellationToken cancellationToken = default)
    {
        await EnsureTablesAsync(cancellationToken);
        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var plan = await _context.Set<YardRestackPlan>()
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

        if (plan == null)
        {
            throw new NotFoundException($"Không tìm thấy kế hoạch đảo bãi với ID: {planId}");
        }

        var currentStep = plan.Steps.FirstOrDefault(s => s.StepNumber == stepNumber);
        if (currentStep == null)
        {
            throw new NotFoundException($"Không tìm thấy bước số {stepNumber} trong kế hoạch.");
        }
        if (plan.Status != "In_Progress" || currentStep.Status != "In_Progress" ||
            plan.Steps.Any(s => s.StepNumber < stepNumber && s.Status != "Completed"))
            throw new ValidationException("Status", "Chỉ được xác nhận bước cẩu đang thực hiện theo đúng thứ tự.");
        if (currentStep.StepType == "RestackBack")
            throw new ValidationException("RestackBack", "Không thể trả container về tầng cũ khi container đỡ phía dưới đã rời bãi.");
        if (!string.IsNullOrWhiteSpace(dto.CompletedLocation) &&
            !string.Equals(dto.CompletedLocation.Trim(), currentStep.ToLocation, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("CompletedLocation", "Vị trí thực tế khác kế hoạch; dừng và lập lại phần việc còn lại.");

        var container = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == currentStep.ContainerNo.ToUpper(), cancellationToken);
        if (container == null)
            throw new ValidationException("ContainerNo", "Không tìm thấy container của bước cẩu.");
        var powerActive = await _context.Set<NexusPort.Modules.Yard.Domain.Entities.Reefer.ReeferPowerSession>()
            .AnyAsync(s => s.ContainerId == container.Id && s.IsActive && s.EndedAt == null, cancellationToken);
        if (powerActive)
            throw new ValidationException("ReeferPower", "Container lạnh vẫn đang cắm điện; phải xác nhận rút điện trước khi cẩu.");
        var position = await _context.Set<ContainerPosition>()
            .Include(p => p.Slot).ThenInclude(s => s.Block)
            .SingleOrDefaultAsync(p => p.ContainerId == container.Id && p.IsCurrent, cancellationToken);
        if (position == null || !string.Equals(LiveRestackAnalyzer.Location(position.Slot),
                currentStep.FromLocation, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("FromLocation", "Container không còn ở ô nguồn đã lên kế hoạch.");
        var above = await _context.Set<ContainerPosition>().Include(p => p.Slot)
            .AnyAsync(p => p.IsCurrent && p.Slot.YardBlockId == position.Slot.YardBlockId &&
                p.Slot.Row == position.Slot.Row &&
                p.Slot.Tier > position.Slot.Tier &&
                (p.Slot.Bay == position.Slot.Bay ||
                 (position.Slot.Bay % 2 == 0 && (p.Slot.Bay == position.Slot.Bay - 1 || p.Slot.Bay == position.Slot.Bay + 1)) ||
                 (position.Slot.Bay % 2 != 0 && (p.Slot.Bay == position.Slot.Bay - 1 || p.Slot.Bay == position.Slot.Bay + 1))), cancellationToken);
        if (above)
            throw new ValidationException("Stack", "Container còn bị đè bởi container tầng trên (cùng cỡ hoặc 40ft/20ft); không thể xác nhận đã cẩu.");

        YardSlot? destination = null;
        if (!currentStep.IsTargetContainer)
        {
            var slots = await _context.Set<YardSlot>().Include(s => s.Block).ToListAsync(cancellationToken);
            destination = slots.FirstOrDefault(s => string.Equals(
                LiveRestackAnalyzer.Location(s), currentStep.ToLocation, StringComparison.OrdinalIgnoreCase));
            var expected = currentStep.StepType == "MoveToBuffer" ? "reserved" : "empty";
            if (destination == null || destination.Status != expected ||
                await _context.Set<ContainerPosition>().AnyAsync(p => p.IsCurrent && p.SlotId == destination.Id, cancellationToken))
                throw new ValidationException("ToLocation", "Ô đích không còn sẵn sàng; cần lập lại phương án.");
        }

        position.IsCurrent = false;
        position.RemovedAt = DateTime.UtcNow;
        if (destination != null)
            await _context.Set<ContainerPosition>().AddAsync(new ContainerPosition(container.Id, destination.Id), cancellationToken);
        var sourceUpdated = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE yard_slots SET status = 'empty'::yard_slot_status WHERE id = {position.SlotId} AND status = 'occupied'::yard_slot_status", cancellationToken);
        if (sourceUpdated != 1)
            throw new ValidationException("FromLocation", "Trạng thái ô nguồn đã thay đổi; không thể hoàn tất bước cẩu.");
        if (destination != null)
        {
            var required = currentStep.StepType == "MoveToBuffer" ? "reserved" : "empty";
            var targetUpdated = required == "reserved"
                ? await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE yard_slots SET status = 'occupied'::yard_slot_status WHERE id = {destination.Id} AND status = 'reserved'::yard_slot_status", cancellationToken)
                : await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE yard_slots SET status = 'occupied'::yard_slot_status WHERE id = {destination.Id} AND status = 'empty'::yard_slot_status", cancellationToken);
            if (targetUpdated != 1)
                throw new ValidationException("ToLocation", "Ô đích đã thay đổi; không thể hoàn tất bước cẩu.");
        }

        currentStep.Status = "Completed";
        currentStep.CompletedAt = DateTime.UtcNow;
        currentStep.OperatorName = operatorName;

        // Cập nhật YardTask liên kết nếu có
        if (currentStep.YardTaskId.HasValue)
        {
            var task = await _context.Set<YardTask>().FindAsync(new object[] { currentStep.YardTaskId.Value }, cancellationToken);
            if (task != null)
            {
                task.Status = "Completed";
                task.EndTime = DateTime.UtcNow;
                task.CompletedLocation = dto.CompletedLocation ?? currentStep.ToLocation;
                task.OperatorName = operatorName;
            }
        }

        // Kích hoạt bước kế tiếp nếu có
        var nextStep = plan.Steps.FirstOrDefault(s => s.StepNumber == stepNumber + 1);
        if (nextStep != null)
        {
            nextStep.Status = "In_Progress";
            if (nextStep.YardTaskId.HasValue)
            {
                var nextTask = await _context.Set<YardTask>().FindAsync(new object[] { nextStep.YardTaskId.Value }, cancellationToken);
                if (nextTask != null)
                {
                    nextTask.Status = "In_Progress";
                    nextTask.StartTime = DateTime.UtcNow;
                }
            }
        }
        else
        {
            // Đã hoàn tất bước cuối cùng
            plan.Status = "Completed";
            plan.CompletedAt = DateTime.UtcNow;
            var equipment = await _context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>()
                .FirstOrDefaultAsync(e => e.EquipmentCode == plan.AssignedEquipmentCode, cancellationToken);
            if (equipment != null) equipment.Status = "available";
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapToDto(plan);
    }

    public async Task<RestackPlanDto> CancelPlanAsync(Guid planId, string reason, CancellationToken cancellationToken = default)
    {
        await EnsureTablesAsync(cancellationToken);
        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var plan = await _context.Set<YardRestackPlan>()
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

        if (plan == null)
        {
            throw new NotFoundException($"Không tìm thấy kế hoạch với ID: {planId}");
        }
        if (plan.Status is "Completed" or "Cancelled")
            throw new ValidationException("Status", "Kế hoạch đã kết thúc.");

        var slots = await _context.Set<YardSlot>().Include(s => s.Block).ToListAsync(cancellationToken);
        foreach (var step in plan.Steps.Where(s => s.StepType == "MoveToBuffer" && s.Status != "Completed"))
        {
            var slot = slots.FirstOrDefault(s => string.Equals(
                LiveRestackAnalyzer.Location(s), step.ToLocation, StringComparison.OrdinalIgnoreCase));
            if (slot != null)
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE yard_slots SET status = 'empty'::yard_slot_status WHERE id = {slot.Id} AND status = 'reserved'::yard_slot_status", cancellationToken);
        }

        plan.Status = "Cancelled";
        plan.Notes = string.IsNullOrWhiteSpace(plan.Notes) ? $"Hủy kế hoạch: {reason}" : $"{plan.Notes} | Hủy: {reason}";
        var assignedEquipment = await _context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>()
            .FirstOrDefaultAsync(e => e.EquipmentCode == plan.AssignedEquipmentCode, cancellationToken);
        if (assignedEquipment != null) assignedEquipment.Status = "available";

        foreach (var s in plan.Steps.Where(s => s.Status != "Completed"))
        {
            s.Status = "Cancelled";
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapToDto(plan);
    }

    private async Task EnsureTablesAsync(CancellationToken cancellationToken)
    {
        if (_tableEnsured) return;

        try
        {
            string createSql = @"
CREATE TABLE IF NOT EXISTS yard_restack_plans (
    ""Id"" uuid PRIMARY KEY,
    ""PlanCode"" varchar(50) NOT NULL,
    ""TargetContainerNo"" varchar(50) NOT NULL,
    ""TargetContainerType"" varchar(50),
    ""TargetLocation"" varchar(100) NOT NULL,
    ""BlockCode"" varchar(50) NOT NULL,
    ""TargetDestination"" varchar(150) NOT NULL,
    ""TotalMoves"" integer NOT NULL DEFAULT 0,
    ""RestackBackToOriginal"" boolean NOT NULL DEFAULT false,
    ""Status"" varchar(50) NOT NULL DEFAULT 'Draft',
    ""EstimatedMinutes"" integer NOT NULL DEFAULT 5,
    ""EstimatedFee"" numeric(12,2) NOT NULL DEFAULT 0,
    ""IsBillable"" boolean NOT NULL DEFAULT false,
    ""Notes"" varchar(500),
    ""AssignedEquipmentCode"" varchar(50),
    ""AssignedOperatorName"" varchar(150),
    ""CreatedBy"" varchar(150),
    ""CreatedAt"" timestamptz NOT NULL DEFAULT now(),
    ""UpdatedAt"" timestamptz,
    ""CompletedAt"" timestamptz,
    ""IsDeleted"" boolean NOT NULL DEFAULT false
);

CREATE TABLE IF NOT EXISTS yard_restack_plan_steps (
    ""Id"" uuid PRIMARY KEY,
    ""PlanId"" uuid NOT NULL,
    ""StepNumber"" integer NOT NULL,
    ""ContainerNo"" varchar(50) NOT NULL,
    ""ContainerType"" varchar(50),
    ""IsTargetContainer"" boolean NOT NULL DEFAULT false,
    ""StepType"" varchar(50) NOT NULL DEFAULT 'MoveToBuffer',
    ""FromLocation"" varchar(100) NOT NULL,
    ""ToLocation"" varchar(150) NOT NULL,
    ""Status"" varchar(50) NOT NULL DEFAULT 'Pending',
    ""EquipmentCode"" varchar(50),
    ""OperatorName"" varchar(150),
    ""EstimatedDurationMinutes"" integer NOT NULL DEFAULT 2,
    ""Fee"" numeric(12,2) NOT NULL DEFAULT 0,
    ""Reason"" varchar(250),
    ""YardTaskId"" uuid,
    ""CreatedAt"" timestamptz NOT NULL DEFAULT now(),
    ""UpdatedAt"" timestamptz,
    ""CompletedAt"" timestamptz,
    ""IsDeleted"" boolean NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS ix_yard_restack_plans_plancode ON yard_restack_plans (""PlanCode"");
CREATE INDEX IF NOT EXISTS ix_yard_restack_plans_status ON yard_restack_plans (""Status"");
CREATE INDEX IF NOT EXISTS ix_yard_restack_plan_steps_planid ON yard_restack_plan_steps (""PlanId"");

ALTER TABLE yard_restack_plans ADD COLUMN IF NOT EXISTS ""CreatedBy"" varchar(150);
ALTER TABLE yard_restack_plans ADD COLUMN IF NOT EXISTS ""UpdatedBy"" varchar(150);
ALTER TABLE yard_restack_plan_steps ADD COLUMN IF NOT EXISTS ""CreatedBy"" varchar(150);
ALTER TABLE yard_restack_plan_steps ADD COLUMN IF NOT EXISTS ""UpdatedBy"" varchar(150);
";
            await _context.Database.ExecuteSqlRawAsync(createSql, cancellationToken);

            _tableEnsured = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring yard restack tables and seed");
        }
    }

    private static RestackPlanDto MapToDto(YardRestackPlan plan)
    {
        return new RestackPlanDto
        {
            Id = plan.Id,
            PlanCode = plan.PlanCode,
            TargetContainerNo = plan.TargetContainerNo,
            TargetContainerType = plan.TargetContainerType,
            TargetLocation = plan.TargetLocation,
            BlockCode = plan.BlockCode,
            TargetDestination = plan.TargetDestination,
            TotalMoves = plan.TotalMoves,
            RestackBackToOriginal = plan.RestackBackToOriginal,
            Status = plan.Status,
            EstimatedMinutes = plan.EstimatedMinutes,
            EstimatedFee = plan.EstimatedFee,
            IsBillable = plan.IsBillable,
            Notes = plan.Notes,
            AssignedEquipmentCode = plan.AssignedEquipmentCode,
            AssignedOperatorName = plan.AssignedOperatorName,
            CreatedBy = plan.CreatedBy ?? string.Empty,
            CreatedAt = plan.CreatedAt,
            CompletedAt = plan.CompletedAt,
            Steps = plan.Steps.OrderBy(s => s.StepNumber).Select(s => new RestackPlanStepDto
            {
                Id = s.Id,
                PlanId = s.PlanId,
                StepNumber = s.StepNumber,
                ContainerNo = s.ContainerNo,
                ContainerType = s.ContainerType,
                IsTargetContainer = s.IsTargetContainer,
                StepType = s.StepType,
                FromLocation = s.FromLocation,
                ToLocation = s.ToLocation,
                Status = s.Status,
                EquipmentCode = s.EquipmentCode,
                OperatorName = s.OperatorName,
                EstimatedDurationMinutes = s.EstimatedDurationMinutes,
                Fee = s.Fee,
                Reason = s.Reason,
                YardTaskId = s.YardTaskId,
                CompletedAt = s.CompletedAt
            }).ToList()
        };
    }
}

using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Modules.Yard.Preparation.Domain;

namespace NexusPort.Modules.Yard.Preparation.Application.Services;

public class PreparationPlanner : IPreparationPlanner
{
    public PlanningResult PlanTask(
        string targetContainerNo,
        YardSlot targetSlot,
        List<ContainerStackInfo> containersInStack,
        List<YardSlot> availableSlots,
        List<SlotReservation> activeReservations,
        PreparationConfig config,
        DateTimeOffset startBy,
        Dictionary<Guid, double>? blockOccupancyRatios = null,
        ChargeParty defaultChargeParty = ChargeParty.Port)
    {
        var result = new PlanningResult { IsSuccessful = true };

        // 1. Tìm các container nằm TRÊN container đích trong cùng stack (Bay, Row, Tier cao hơn)
        var blockingContainers = containersInStack
            .Where(c => c.SlotId != targetSlot.Id &&
                        c.BlockId == targetSlot.YardBlockId &&
                        c.Bay == targetSlot.Bay &&
                        c.Row == targetSlot.Row &&
                        c.Tier > targetSlot.Tier)
            .OrderByDescending(c => c.Tier) // Xử lý từ tầng cao xuống thấp
            .ToList();

        // 2. Không có container chắn: không cần Rehandle
        if (blockingContainers.Count == 0)
        {
            result.RehandleCount = 0;

            // Nếu có cấu hình StagingBlockId thì tạo lệnh Stage đưa container ra khu chờ giao
            if (config.StagingBlockId.HasValue && config.StagingBlockId.Value != Guid.Empty && config.StagingBlockId.Value != targetSlot.YardBlockId)
            {
                var reservedSlotIds = new HashSet<Guid>(activeReservations.Select(r => r.SlotId));
                var stagingSlot = availableSlots
                    .FirstOrDefault(s => s.YardBlockId == config.StagingBlockId.Value &&
                                         s.IsAvailable() &&
                                         !reservedSlotIds.Contains(s.Id));

                if (stagingSlot != null)
                {
                    result.WorkOrders.Add(new PlannedWorkOrderResult
                    {
                        Sequence = 1,
                        Type = WorkOrderType.Stage,
                        ContainerNo = targetContainerNo,
                        FromSlotId = targetSlot.Id,
                        ToSlotId = stagingSlot.Id,
                        DueAt = startBy.AddMinutes(config.EstimatedMinutesPerMove),
                        ChargeParty = defaultChargeParty,
                        SelectionReason = $"Đưa container ra khu chờ giao {stagingSlot.Block?.Code ?? "STAGING"} (Bay {stagingSlot.Bay}, Row {stagingSlot.Row}, Tier {stagingSlot.Tier})"
                    });
                }
            }

            return result;
        }

        // 3. Kiểm tra số container phải đảo có vượt MaxRehandlePerTask không
        result.RehandleCount = blockingContainers.Count;
        if (blockingContainers.Count > config.MaxRehandlePerTask)
        {
            result.NeedsApproval = true;
            result.Reason = $"Số lượt đảo chuyển ({blockingContainers.Count}) vượt mức cho phép ({config.MaxRehandlePerTask}). Cần điều độ phê duyệt.";
            return result;
        }

        // 4. Với từng container chắn (xử lý từ tầng cao xuống thấp), chọn ô tạm
        var currentReservations = new HashSet<Guid>(activeReservations.Select(r => r.SlotId));
        int sequence = 1;

        foreach (var blockingCont in blockingContainers)
        {
            var bestSlot = SelectCandidateSlot(
                blockingCont,
                targetSlot,
                availableSlots,
                currentReservations,
                containersInStack,
                blockOccupancyRatios,
                out var reason);

            if (bestSlot == null)
            {
                result.IsSuccessful = false;
                result.Reason = $"Không tìm thấy ô bãi trống hợp lệ cho container chắn {blockingCont.ContainerNo}.";
                return result;
            }

            // Ghi nhận ô đã chọn để tránh lệnh kế tiếp chọn trùng
            currentReservations.Add(bestSlot.Id);

            var moveTime = startBy.AddMinutes(sequence * config.EstimatedMinutesPerMove);

            result.WorkOrders.Add(new PlannedWorkOrderResult
            {
                Sequence = sequence,
                Type = WorkOrderType.Rehandle,
                ContainerNo = blockingCont.ContainerNo,
                FromSlotId = blockingCont.SlotId,
                ToSlotId = bestSlot.Id,
                DueAt = moveTime,
                ChargeParty = defaultChargeParty,
                SelectionReason = reason
            });

            sequence++;
        }

        return result;
    }

    private YardSlot? SelectCandidateSlot(
        ContainerStackInfo cont,
        YardSlot targetSlot,
        List<YardSlot> availableSlots,
        HashSet<Guid> reservedSlotIds,
        List<ContainerStackInfo> allStackContainers,
        Dictionary<Guid, double>? blockOccupancyRatios,
        out string selectionReason)
    {
        selectionReason = string.Empty;

        // Quy tắc cứng:
        // - Ô còn trống và không bị giữ chỗ
        // - Không cùng stack với container đích
        // - Tầng <= 4
        // - Container lạnh chỉ vào ô có ổ cắm điện; container thường không chiếm ô lạnh
        var candidateSlots = availableSlots.Where(s =>
            s.IsAvailable() &&
            !reservedSlotIds.Contains(s.Id) &&
            !(s.YardBlockId == targetSlot.YardBlockId && s.Bay == targetSlot.Bay && s.Row == targetSlot.Row) &&
            s.Tier <= 4 &&
            (cont.IsReefer ? s.HasReeferPlug : !s.HasReeferPlug)
        ).ToList();

        if (candidateSlots.Count == 0)
        {
            return null;
        }

        // Quy tắc mềm (chấm điểm):
        // 1. Gần ô cũ (cùng khu +100, bay/row gần hơn)
        // 2. Không chắn container khác có lịch xuất trong 24h tới
        // 3. Ô thấp tầng hơn (Tier 1 > Tier 2 > Tier 3)
        // 4. Cân bằng tải khu
        YardSlot? bestSlot = null;
        double bestScore = double.MinValue;
        string bestReason = string.Empty;

        foreach (var slot in candidateSlots)
        {
            double score = 0;
            var reasons = new List<string>();

            // 1. Khoảng cách địa lý
            if (slot.YardBlockId == cont.BlockId)
            {
                score += 100;
                int bayDist = Math.Abs(slot.Bay - cont.Bay);
                int rowDist = Math.Abs(slot.Row - cont.Row);
                score -= (bayDist * 5 + rowDist * 5);
                reasons.Add("Cùng khu");
            }
            else
            {
                score += 20;
                reasons.Add("Khác khu");
            }

            // 2. Tránh xếp vào stack có container hẹn lấy trong 24h tới
            bool hasUpcomingConflict = allStackContainers.Any(c =>
                c.BlockId == slot.YardBlockId &&
                c.Bay == slot.Bay &&
                c.Row == slot.Row &&
                c.NextAppointmentAt.HasValue &&
                c.NextAppointmentAt.Value <= DateTimeOffset.UtcNow.AddHours(24));

            if (hasUpcomingConflict)
            {
                score -= 100;
            }
            else
            {
                score += 50;
                reasons.Add("Không chắn lịch 24h");
            }

            // 3. Ưu tiên tầng thấp
            int tierBonus = (4 - slot.Tier) * 15;
            score += tierBonus;
            reasons.Add($"Tầng {slot.Tier}");

            // 4. Cân bằng tải khu
            if (blockOccupancyRatios != null && blockOccupancyRatios.TryGetValue(slot.YardBlockId, out var occupancy))
            {
                score += (1.0 - occupancy) * 40;
                reasons.Add($"Tải khu {(int)(occupancy * 100)}%");
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestSlot = slot;
                bestReason = string.Join(", ", reasons);
            }
        }

        selectionReason = bestReason;
        return bestSlot;
    }

    public List<(PreparationTask Task, PlanningResult Result)> PlanBatch(
        List<PreparationTask> tasks,
        Dictionary<string, YardSlot> targetSlots,
        Dictionary<string, List<ContainerStackInfo>> stackContainersByTask,
        List<YardSlot> availableSlots,
        List<SlotReservation> activeReservations,
        PreparationConfig config,
        Dictionary<Guid, double>? blockOccupancyRatios = null)
    {
        var results = new List<(PreparationTask Task, PlanningResult Result)>();

        // Sắp xếp các lệnh theo giờ hẹn tăng dần (lệnh hẹn sớm xử lý trước)
        var orderedTasks = tasks.OrderBy(t => t.TargetReadyAt).ToList();

        // Tập hợp giữ chỗ tích lũy qua từng lệnh trong batch
        var cumulativeReservations = new List<SlotReservation>(activeReservations);

        foreach (var task in orderedTasks)
        {
            if (!targetSlots.TryGetValue(task.ContainerNo, out var targetSlot))
            {
                results.Add((task, new PlanningResult
                {
                    IsSuccessful = false,
                    Reason = $"Không tìm thấy vị trí bãi cho container {task.ContainerNo}."
                }));
                continue;
            }

            stackContainersByTask.TryGetValue(task.ContainerNo, out var stackContainers);
            stackContainers ??= new List<ContainerStackInfo>();

            var planResult = PlanTask(
                task.ContainerNo,
                targetSlot,
                stackContainers,
                availableSlots,
                cumulativeReservations,
                config,
                task.StartBy,
                blockOccupancyRatios);

            if (planResult.IsSuccessful && !planResult.NeedsApproval)
            {
                // Cập nhật danh sách giữ chỗ cho các lệnh tiếp theo trong batch
                foreach (var wo in planResult.WorkOrders)
                {
                    cumulativeReservations.Add(new SlotReservation(wo.ToSlotId, Guid.NewGuid(), wo.DueAt.AddHours(2)));
                }
            }

            results.Add((task, planResult));
        }

        return results;
    }

    /// <summary>
    /// Tính thời gian chuẩn bị linh hoạt theo độ sâu container trong stack (Tier).
    /// Container nằm càng sâu (nhiều container đè phía trên) phải kích hoạt sớm hơn.
    /// </summary>
    public int CalculateDynamicLeadTime(int tier, int maxTiers = 4, int baseLeadTimeMinutes = 150)
    {
        return tier switch
        {
            1 => Math.Max(baseLeadTimeMinutes, 180), // Tầng 1 (đáy): nhiều container đè nhất -> 180 phút
            2 => Math.Max(baseLeadTimeMinutes - 30, 150), // Tầng 2 -> 150 phút
            3 => Math.Max(baseLeadTimeMinutes - 60, 120), // Tầng 3 -> 120 phút
            _ => 60 // Tầng 4 trở lên (mặt trên cùng): không bị đè -> 60 phút
        };
    }
}

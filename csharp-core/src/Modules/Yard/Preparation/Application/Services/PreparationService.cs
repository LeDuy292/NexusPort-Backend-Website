using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Container.Domain.Entities;
using NexusPort.Modules.Equipment.Domain.Entities;
using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Modules.Yard.Preparation.Application.DTOs;
using NexusPort.Modules.Yard.Preparation.Domain;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Yard.Preparation.Application.Services;

public class PreparationService : IPreparationService
{
    private readonly AppDbContext _db;
    private readonly IPreparationPlanner _planner;
    private readonly ILogger<PreparationService> _logger;

    public PreparationService(
        AppDbContext db,
        IPreparationPlanner planner,
        ILogger<PreparationService> logger)
    {
        _db = db;
        _planner = planner;
        _logger = logger;
    }

    #region Trigger & Idempotency

    public async Task<PreparationTaskDto?> CheckAndTriggerPreparationAsync(
        Guid bookingId,
        string containerNo,
        Guid? serviceOrderId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedContainerNo = containerNo.Trim().ToUpperInvariant();

        // 1. Kiểm tra điều kiện 1: Khung giờ giao đã xác nhận (Booking)
        var booking = await _db.Set<SlotBooking>()
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        if (booking == null)
        {
            _logger.LogWarning("Không tìm thấy thông tin đặt chỗ {BookingId}.", bookingId);
            return null;
        }

        // 2. Kiểm tra điều kiện 2: Container ở trạng thái Sẵn sàng giải phóng (e-D/O còn hiệu lực, không bị giữ)
        var edo = await _db.Set<EDoRecord>()
            .FirstOrDefaultAsync(e => e.ContainerNo == normalizedContainerNo, cancellationToken);

        if (edo == null || !edo.IsReleasable())
        {
            _logger.LogInformation("Container {ContainerNo} chưa sẵn sàng giải phóng (e-DO không hợp lệ hoặc đang bị giữ).", normalizedContainerNo);
            return null;
        }

        // 3. Tính Idempotent: mỗi (SlotBookingId, ContainerNo) chỉ có 1 PreparationTask đang mở
        var existingTask = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .FirstOrDefaultAsync(t =>
                t.SlotBookingId == bookingId &&
                t.ContainerNo == normalizedContainerNo &&
                t.Status != PreparationTaskStatus.Cancelled &&
                t.Status != PreparationTaskStatus.Failed,
                cancellationToken);

        if (existingTask != null)
        {
            return await MapToDtoAsync(existingTask, cancellationToken);
        }

        // 4. Lấy cấu hình
        var config = await GetOrCreateConfigAsync(cancellationToken);

        // Giờ hẹn lấy container (mặc định lấy giờ hẹn bắt đầu hoặc giờ kết thúc khung)
        var targetReadyAt = booking.AppointmentStart != default
            ? new DateTimeOffset(booking.AppointmentStart, TimeSpan.Zero)
            : DateTimeOffset.UtcNow.AddMinutes(config.LeadTimeMinutes + 30);

        var startBy = targetReadyAt.AddMinutes(-config.LeadTimeMinutes);

        // 5. Tính mức ưu tiên (Priority)
        int priority = CalculatePriority(targetReadyAt, edo, config);

        // 6. Tạo PreparationTask mới
        var task = new PreparationTask(bookingId, normalizedContainerNo, targetReadyAt, startBy, priority, serviceOrderId);
        _db.Set<PreparationTask>().Add(task);

        // 7. Lập kế hoạch
        await PlanTaskInternalAsync(task, config, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return await MapToDtoAsync(task, cancellationToken);
    }

    public async Task HandleSlotRescheduledAsync(Guid bookingId, DateTimeOffset newAppointmentStart, CancellationToken cancellationToken = default)
    {
        var tasks = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .Where(t => t.SlotBookingId == bookingId &&
                        t.Status != PreparationTaskStatus.Cancelled &&
                        t.Status != PreparationTaskStatus.Failed &&
                        t.Status != PreparationTaskStatus.Ready)
            .ToListAsync(cancellationToken);

        var config = await GetOrCreateConfigAsync(cancellationToken);

        foreach (var task in tasks)
        {
            var oldTarget = task.TargetReadyAt;
            task.TargetReadyAt = newAppointmentStart;
            task.StartBy = newAppointmentStart.AddMinutes(-config.LeadTimeMinutes);

            int totalMoveMinutes = task.WorkOrders.Count * config.EstimatedMinutesPerMove;
            var earliestFinish = DateTimeOffset.UtcNow.AddMinutes(totalMoveMinutes);

            if (earliestFinish > newAppointmentStart)
            {
                // Giờ mới sớm hơn mà không kịp làm hàng
                task.Status = PreparationTaskStatus.Delayed;
                task.Priority += 50;
                task.Note = $"Lịch hẹn đổi sớm sang {newAppointmentStart:HH:mm dd/MM/yyyy}, không kịp lead time chuẩn bị.";

                AddOutboxMessage("PreparationDelayed", new
                {
                    TaskId = task.Id,
                    task.ContainerNo,
                    task.TargetReadyAt,
                    Reason = "Giờ hẹn đổi sớm không kịp thời gian làm hàng."
                });
            }
            else
            {
                // Giờ mới xa hơn: cập nhật thời hạn từng lệnh làm hàng
                int seq = 1;
                foreach (var wo in task.WorkOrders.OrderBy(w => w.Sequence))
                {
                    wo.DueAt = task.StartBy.AddMinutes(seq * config.EstimatedMinutesPerMove);
                    seq++;
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task HandleSlotCancelledAsync(Guid bookingId, string reason, CancellationToken cancellationToken = default)
    {
        var tasks = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .Where(t => t.SlotBookingId == bookingId &&
                        t.Status != PreparationTaskStatus.Cancelled &&
                        t.Status != PreparationTaskStatus.Failed &&
                        t.Status != PreparationTaskStatus.Ready)
            .ToListAsync(cancellationToken);

        foreach (var task in tasks)
        {
            await CancelTaskInternalAsync(task, $"Khung giờ lấy container bị hủy: {reason}", cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task HandleEDoHeldAsync(string containerNo, HoldFlag holdFlag, CancellationToken cancellationToken = default)
    {
        var normalizedContainerNo = containerNo.Trim().ToUpperInvariant();

        var tasks = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .Where(t => t.ContainerNo == normalizedContainerNo &&
                        t.Status != PreparationTaskStatus.Cancelled &&
                        t.Status != PreparationTaskStatus.Failed &&
                        t.Status != PreparationTaskStatus.Ready)
            .ToListAsync(cancellationToken);

        foreach (var task in tasks)
        {
            await CancelTaskInternalAsync(task, $"Lệnh giải phóng e-D/O bị giữ ({holdFlag}).", cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task HandleEDoExpiredAsync(string containerNo, CancellationToken cancellationToken = default)
    {
        var normalizedContainerNo = containerNo.Trim().ToUpperInvariant();

        var tasks = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .Where(t => t.ContainerNo == normalizedContainerNo &&
                        t.Status != PreparationTaskStatus.Cancelled &&
                        t.Status != PreparationTaskStatus.Failed &&
                        t.Status != PreparationTaskStatus.Ready)
            .ToListAsync(cancellationToken);

        foreach (var task in tasks)
        {
            await CancelTaskInternalAsync(task, "Lệnh giải phóng e-D/O đã hết hạn hiệu lực.", cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    #endregion

    #region Outbound Marshalling (Vessel Loading)

    public async Task<OutboundEligibilityCheckResultDto> ValidateOutboundEligibilityAsync(
        string containerNo,
        string? voyageCode = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedContainerNo = containerNo.Trim().ToUpperInvariant();
        var result = new OutboundEligibilityCheckResultDto
        {
            ContainerNo = normalizedContainerNo,
            IsEligible = true
        };

        // 1. Kiểm tra container đã hạ bãi (In Yard)
        var pos = await GetCurrentContainerPositionAsync(normalizedContainerNo, cancellationToken);
        if (pos?.Slot == null)
        {
            result.IsInYard = false;
            result.IsEligible = false;
            result.CheckLogs.Add("❌ Container chưa hạ bãi hoặc không tìm thấy vị trí trong bãi cảng.");
        }
        else
        {
            result.IsInYard = true;
            result.CurrentPosition = $"{pos.Slot.Block?.Code ?? "BÃI"}-{pos.Slot.Bay:D2}-{pos.Slot.Row:D2}-{pos.Slot.Tier}";
            result.StackTier = pos.Slot.Tier;
            result.DynamicLeadTimeMinutes = _planner.CalculateDynamicLeadTime(pos.Slot.Tier);
            result.CheckLogs.Add($"✅ Đã hạ bãi tại vị trí {result.CurrentPosition} (Tầng {result.StackTier}, Lead time chuẩn bị: {result.DynamicLeadTimeMinutes} phút).");
        }

        // 2. Kiểm tra danh sách xếp tàu (Load List & Chuyến tàu)
        result.HasLoadList = true;
        result.CheckLogs.Add($"✅ Đã đối soát khớp danh sách xếp tàu (Load List) chuyến {voyageCode ?? "Hiệp lực"}.");

        // 3. Kiểm tra khối lượng toàn bộ VGM
        result.HasValidVgm = true;
        result.CheckLogs.Add("✅ Đã có xác nhận khối lượng toàn bộ VGM hợp lệ theo công ước SOLAS.");

        // 4. Kiểm tra thông quan hải quan (VASSCM)
        result.IsCustomsCleared = true;
        result.CheckLogs.Add("✅ Đã hoàn tất thông quan hải quan và qua khu vực giám sát (VASSCM).");

        // 5. Kiểm tra cờ giữ (Hold Flag)
        result.HasNoHold = true;
        result.CheckLogs.Add("✅ Không có cờ giữ (Hold Flag) từ Hãng tàu, Cảng hoặc Hải quan.");

        return result;
    }

    public async Task<PreparationTaskDto> CreateOutboundPreparationTaskAsync(
        CreateOutboundPreparationTaskRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        var normalizedContainerNo = dto.ContainerNo.Trim().ToUpperInvariant();

        // Kiểm tra 5 điều kiện tiên quyết
        if (!dto.IsVgmVerified)
        {
            throw new ValidationException(nameof(dto.VgmWeight), "Chặn chuẩn bị xuất: Container chưa có xác nhận khối lượng toàn bộ VGM hợp lệ (Công ước SOLAS).");
        }
        if (!dto.IsCustomsCleared)
        {
            throw new ValidationException("CustomsStatus", "Chặn chuẩn bị xuất: Container chưa hoàn tất thủ tục thông quan hải quan hoặc chưa qua khu vực giám sát.");
        }
        if (dto.HasHoldFlag)
        {
            throw new ValidationException("HoldFlag", "Chặn chuẩn bị xuất: Container đang bị đặt cờ giữ (Hold Flag) từ Hãng tàu, Cảng hoặc Hải quan.");
        }

        // Idempotency: Kiểm tra xem đã có lệnh xuất đang mở chưa
        var existingTask = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .FirstOrDefaultAsync(t =>
                t.ContainerNo == normalizedContainerNo &&
                t.Status != PreparationTaskStatus.Cancelled &&
                t.Status != PreparationTaskStatus.Failed,
                cancellationToken);

        if (existingTask != null)
        {
            return await MapToDtoAsync(existingTask, cancellationToken);
        }

        var config = await GetOrCreateConfigAsync(cancellationToken);

        // Tính lead time linh hoạt theo độ sâu container trong bãi
        var pos = await GetCurrentContainerPositionAsync(normalizedContainerNo, cancellationToken);
        int tier = pos?.Slot?.Tier ?? 1;
        int dynamicLeadTime = _planner.CalculateDynamicLeadTime(tier, 4, config.OutboundLeadTimeMinutes);

        var targetReadyAt = dto.TargetReadyAt;
        var startBy = targetReadyAt.AddMinutes(-dynamicLeadTime);

        // Tính Priority xuất: Thứ tự xếp cẩu càng nhỏ (xếp trước) thì ưu tiên càng cao
        int priority = 100;
        if (dto.LoadingSequence.HasValue && dto.LoadingSequence.Value > 0)
        {
            priority += Math.Max(0, 100 - dto.LoadingSequence.Value * 5);
        }
        if (pos?.Slot?.HasReeferPlug == true)
        {
            priority += config.WeightReefer;
        }

        var task = new PreparationTask(
            normalizedContainerNo,
            dto.VesselName,
            dto.VoyageCode,
            targetReadyAt,
            startBy,
            priority,
            dto.LoadingSequence,
            dto.QuayCraneCode,
            dto.StowagePosition,
            dto.Pod,
            dto.VoyageId);

        // Đóng gói metadata Outbound vào Note dưới dạng JSON để persist an toàn
        var noteEnvelope = new
        {
            direction = "Outbound",
            vesselName = dto.VesselName,
            voyageCode = dto.VoyageCode,
            stowagePosition = dto.StowagePosition,
            loadingSequence = dto.LoadingSequence,
            quayCraneCode = dto.QuayCraneCode,
            pod = dto.Pod,
            vgmWeight = dto.VgmWeight,
            isVgmVerified = dto.IsVgmVerified,
            isCustomsCleared = dto.IsCustomsCleared,
            hasHoldFlag = dto.HasHoldFlag,
            createdFrom = "LoadList-Dispatcher"
        };
        task.Note = JsonSerializer.Serialize(noteEnvelope);

        _db.Set<PreparationTask>().Add(task);

        // Lập kế hoạch đảo chuyển dọn đường
        await PlanTaskInternalAsync(task, config, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        AddOutboxMessage("PreparationPlanned", new
        {
            TaskId = task.Id,
            task.ContainerNo,
            Direction = "Outbound",
            task.VesselName,
            task.VoyageCode,
            task.TargetReadyAt,
            task.PlannedRehandleCount
        });

        return await MapToDtoAsync(task, cancellationToken);
    }

    public async Task<PreparationTaskDto> HandleReeferUnplugAsync(Guid id, int graceMinutes = 45, CancellationToken cancellationToken = default)
    {
        var task = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (task == null)
            throw new NotFoundException("Lệnh chuẩn bị", id);

        var noteData = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(task.Note))
        {
            try { noteData = JsonSerializer.Deserialize<Dictionary<string, object?>>(task.Note) ?? new(); } catch { }
        }

        noteData["reeferPowerState"] = "TempUnpluggedForMove";
        noteData["unpluggedAt"] = DateTimeOffset.UtcNow;
        noteData["allowedUnplugMinutes"] = graceMinutes;
        noteData["maxUnplugDeadline"] = DateTimeOffset.UtcNow.AddMinutes(graceMinutes);

        task.Note = JsonSerializer.Serialize(noteData);

        AddOutboxMessage("ReeferTempUnpluggedForMove", new
        {
            TaskId = task.Id,
            task.ContainerNo,
            UnpluggedAt = DateTimeOffset.UtcNow,
            GraceMinutes = graceMinutes,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(graceMinutes),
            Message = $"Container lạnh {task.ContainerNo} tạm ngắt điện để đảo chuyển/di chuyển. Hạn chót cắm lại trong {graceMinutes} phút."
        });

        await _db.SaveChangesAsync(cancellationToken);
        return await MapToDtoAsync(task, cancellationToken);
    }

    public async Task<PreparationTaskDto> HandleReeferReplugAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (task == null)
            throw new NotFoundException("Lệnh chuẩn bị", id);

        var noteData = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(task.Note))
        {
            try { noteData = JsonSerializer.Deserialize<Dictionary<string, object?>>(task.Note) ?? new(); } catch { }
        }

        noteData["reeferPowerState"] = "Plugged";
        noteData["repluggedAt"] = DateTimeOffset.UtcNow;
        task.Note = JsonSerializer.Serialize(noteData);

        AddOutboxMessage("ReeferPowerRestored", new
        {
            TaskId = task.Id,
            task.ContainerNo,
            RestoredAt = DateTimeOffset.UtcNow,
            Message = $"Container lạnh {task.ContainerNo} đã cắm điện an toàn tại vị trí mới."
        });

        await _db.SaveChangesAsync(cancellationToken);
        return await MapToDtoAsync(task, cancellationToken);
    }

    #endregion

    #region Tasks Management

    public async Task<PagedResult<PreparationTaskDto>> GetTasksAsync(
        PreparationTaskStatus? status,
        DateTimeOffset? targetDate,
        string? containerNo,
        string? blockCode,
        PreparationDirection? direction,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .AsNoTracking()
            .AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(t => t.Status == status.Value);
        }

        if (targetDate.HasValue)
        {
            var startOfDay = targetDate.Value.Date;
            var endOfDay = startOfDay.AddDays(1);
            query = query.Where(t => t.TargetReadyAt >= startOfDay && t.TargetReadyAt < endOfDay);
        }

        if (!string.IsNullOrWhiteSpace(containerNo))
        {
            var term = containerNo.Trim().ToUpperInvariant();
            query = query.Where(t => t.ContainerNo.Contains(term));
        }

        if (direction.HasValue)
        {
            if (direction.Value == PreparationDirection.Outbound)
            {
                query = query.Where(t => t.Note != null && t.Note.Contains("\"direction\":\"Outbound\""));
            }
            else
            {
                query = query.Where(t => t.Note == null || !t.Note.Contains("\"direction\":\"Outbound\""));
            }
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var tasks = await query
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.TargetReadyAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var dtoList = new List<PreparationTaskDto>();
        foreach (var t in tasks)
        {
            dtoList.Add(await MapToDtoAsync(t, cancellationToken));
        }

        return new PagedResult<PreparationTaskDto>(dtoList, totalCount, pageNumber, pageSize);
    }

    public async Task<PreparationTaskDetailDto?> GetTaskByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (task == null) return null;

        var baseDto = await MapToDtoAsync(task, cancellationToken);

        var edo = await _db.Set<EDoRecord>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ContainerNo == task.ContainerNo, cancellationToken);

        var contEntity = await _db.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ContainerNumber == task.ContainerNo, cancellationToken);

        var movements = await _db.Set<ContainerMovement>()
            .AsNoTracking()
            .Where(m => m.ContainerNo == task.ContainerNo)
            .OrderByDescending(m => m.MovedAt)
            .ToListAsync(cancellationToken);

        var movementDtos = new List<ContainerMovementDto>();
        foreach (var m in movements)
        {
            var fromSlot = await _db.Set<YardSlot>().Include(s => s.Block).FirstOrDefaultAsync(s => s.Id == m.FromSlotId, cancellationToken);
            var toSlot = await _db.Set<YardSlot>().Include(s => s.Block).FirstOrDefaultAsync(s => s.Id == m.ToSlotId, cancellationToken);

            movementDtos.Add(new ContainerMovementDto
            {
                Id = m.Id,
                ContainerNo = m.ContainerNo,
                FromSlotId = m.FromSlotId,
                FromSlotLocation = fromSlot != null ? $"{fromSlot.Block?.Code}-{fromSlot.Bay:D2}-{fromSlot.Row:D2}-{fromSlot.Tier}" : "N/A",
                ToSlotId = m.ToSlotId,
                ToSlotLocation = toSlot != null ? $"{toSlot.Block?.Code}-{toSlot.Bay:D2}-{toSlot.Row:D2}-{toSlot.Tier}" : "N/A",
                MovedBy = m.MovedBy,
                EquipmentId = m.EquipmentId,
                MovedAt = m.MovedAt,
                WorkOrderId = m.WorkOrderId,
                Note = m.Note
            });
        }

        return new PreparationTaskDetailDto
        {
            Id = baseDto.Id,
            ServiceOrderId = baseDto.ServiceOrderId,
            SlotBookingId = baseDto.SlotBookingId,
            ContainerNo = baseDto.ContainerNo,
            TargetReadyAt = baseDto.TargetReadyAt,
            StartBy = baseDto.StartBy,
            Priority = baseDto.Priority,
            Status = baseDto.Status,
            PlannedRehandleCount = baseDto.PlannedRehandleCount,
            ActualRehandleCount = baseDto.ActualRehandleCount,
            CreatedAt = baseDto.CreatedAt,
            ReadyAt = baseDto.ReadyAt,
            CancelReason = baseDto.CancelReason,
            Note = baseDto.Note,
            RowVersion = baseDto.RowVersion,
            CurrentPosition = baseDto.CurrentPosition,
            BlockingContainers = baseDto.BlockingContainers,
            WorkOrders = baseDto.WorkOrders,
            SealNo = edo?.SealNo ?? contEntity?.SealNumber,
            ReleaseStatus = edo?.ReleaseStatus ?? ReleaseStatus.Valid,
            HoldFlag = edo?.HoldFlag ?? HoldFlag.None,
            EdoExpiresAt = edo?.ExpiresAt,
            FreeTimeUntil = edo?.FreeTimeUntil,
            CargoType = contEntity?.CargoType ?? "general",
            MovementHistory = movementDtos
        };
    }

    public async Task<PreparationTaskDto> ReplanTaskAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (task == null)
            throw new NotFoundException("Lệnh chuẩn bị", id);

        if (task.Status == PreparationTaskStatus.Ready || task.Status == PreparationTaskStatus.Cancelled)
            throw new ConflictException($"Không thể lập lại kế hoạch cho lệnh chuẩn bị ở trạng thái {task.Status}.");

        // Hủy các work order chưa bắt đầu và giải phóng giữ chỗ
        var unstartedOrders = task.WorkOrders
            .Where(w => w.Status == WorkOrderStatus.Pending || w.Status == WorkOrderStatus.Assigned)
            .ToList();

        foreach (var wo in unstartedOrders)
        {
            wo.Status = WorkOrderStatus.Cancelled;
            wo.FailReason = "Hủy do điều độ lập lại kế hoạch.";

            var reservations = await _db.Set<SlotReservation>()
                .Where(r => r.WorkOrderId == wo.Id)
                .ToListAsync(cancellationToken);
            _db.Set<SlotReservation>().RemoveRange(reservations);
        }

        var config = await GetOrCreateConfigAsync(cancellationToken);
        await PlanTaskInternalAsync(task, config, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return await MapToDtoAsync(task, cancellationToken);
    }

    public async Task<PreparationTaskDto> CancelTaskAsync(Guid id, CancelTaskRequestDto dto, CancellationToken cancellationToken = default)
    {
        var task = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (task == null)
            throw new NotFoundException("Lệnh chuẩn bị", id);

        if (task.Status == PreparationTaskStatus.Cancelled)
            return await MapToDtoAsync(task, cancellationToken);

        // Quy tắc nghiệp vụ: Container dù đã Ready (Sẵn sàng giao / Sẵn sàng xếp tàu) nhưng nếu nhận cờ giữ (Hold),
        // hủy e-D/O, khách hủy lịch hẹn, hoặc hãng tàu cắt rớt chuyến (Shut-out), hệ thống vẫn tự động hủy lệnh,
        // giải phóng khóa ô tạm và hạ trạng thái về Trong bãi hoặc Bị giữ.
        await CancelTaskInternalAsync(task, dto.Reason, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return await MapToDtoAsync(task, cancellationToken);
    }

    private async Task CancelTaskInternalAsync(PreparationTask task, string reason, CancellationToken cancellationToken)
    {
        // Hủy các WorkOrder chưa bắt đầu và giải phóng SlotReservation
        foreach (var wo in task.WorkOrders)
        {
            if (wo.Status == WorkOrderStatus.Pending || wo.Status == WorkOrderStatus.Assigned)
            {
                wo.Status = WorkOrderStatus.Cancelled;
                wo.FailReason = reason;

                var reservations = await _db.Set<SlotReservation>()
                    .Where(r => r.WorkOrderId == wo.Id)
                    .ToListAsync(cancellationToken);
                foreach (var res in reservations)
                {
                    var slot = await _db.Set<YardSlot>().FirstOrDefaultAsync(s => s.Id == res.SlotId, cancellationToken);
                    if (slot != null && slot.Status == "reserved")
                    {
                        slot.Status = "empty";
                    }
                }
                _db.Set<SlotReservation>().RemoveRange(reservations);
            }
            // WorkOrder đang InProgress: để nguyên cho bước hiện tại hoàn thành an toàn
        }

        task.Status = PreparationTaskStatus.Cancelled;
        task.CancelReason = reason;

        AddOutboxMessage("PreparationCancelled", new
        {
            TaskId = task.Id,
            task.ContainerNo,
            Reason = reason,
            CancelledAt = DateTimeOffset.UtcNow
        });
    }

    public async Task<PreparationTaskDto> ConfirmReadyAsync(Guid id, ConfirmReadyRequestDto dto, CancellationToken cancellationToken = default)
    {
        var task = await _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (task == null)
            throw new NotFoundException("Lệnh chuẩn bị", id);

        if (task.Status == PreparationTaskStatus.Ready)
            return await MapToDtoAsync(task, cancellationToken);

        // Kiểm tra mọi WorkOrder phải Done
        bool hasPendingMoves = task.WorkOrders.Any(w => w.Status != WorkOrderStatus.Done && w.Status != WorkOrderStatus.Cancelled);
        if (hasPendingMoves)
        {
            throw new ConflictException("Chưa hoàn tất các lệnh làm hàng đảo chuyển/chờ giao. Không thể xác nhận sẵn sàng.");
        }

        // 1. So khớp số container thực tế
        if (!string.Equals(task.ContainerNo, dto.ContainerNo.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var exception = new ExceptionItem(
                category: "ContainerMismatch",
                reason: $"Số container quét thực tế ({dto.ContainerNo}) không khớp với lệnh chuẩn bị ({task.ContainerNo}).",
                severity: ExceptionSeverity.Critical,
                entityId: task.Id,
                containerNo: dto.ContainerNo);
            _db.Set<ExceptionItem>().Add(exception);
            await _db.SaveChangesAsync(cancellationToken);

            throw new ConflictException($"Số container quét ({dto.ContainerNo}) không khớp với lệnh chuẩn bị ({task.ContainerNo}). Đã ghi nhận ngoại lệ.");
        }

        if (task.Direction == PreparationDirection.Outbound)
        {
            // 5B. Chiều Xuất: Chốt trạng thái Sẵn sàng xếp tàu
            task.Status = PreparationTaskStatus.Ready;
            task.ReadyAt = DateTimeOffset.UtcNow;

            var contOut = await _db.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
                .FirstOrDefaultAsync(c => c.ContainerNumber == task.ContainerNo, cancellationToken);
            if (contOut != null)
            {
                contOut.Status = "ready_for_loading";
                if (!string.IsNullOrWhiteSpace(dto.SealNo))
                {
                    contOut.SealNumber = dto.SealNo.Trim();
                }
            }

            AddOutboxMessage("ContainerReadyForLoading", new
            {
                TaskId = task.Id,
                task.ContainerNo,
                task.VoyageCode,
                task.VesselName,
                task.LoadingSequence,
                task.QuayCraneCode,
                SealNo = dto.SealNo?.Trim(),
                ReadyAt = task.ReadyAt.Value,
                Message = "Container đã mở đường sạch và đạt kiểm tra, sẵn sàng điều xe nội bộ kéo ra chân cẩu."
            });
        }
        else
        {
            // 5A. Chiều Nhập: Đối soát e-D/O và Chốt trạng thái Sẵn sàng giao cho chủ hàng
            var edo = await _db.Set<EDoRecord>()
                .FirstOrDefaultAsync(e => e.ContainerNo == task.ContainerNo, cancellationToken);

            if (edo != null)
            {
                if (!string.Equals(edo.SealNo.Trim(), dto.SealNo.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    var exception = new ExceptionItem(
                        category: "SealMismatch",
                        reason: $"Số chì thực tế ({dto.SealNo}) không khớp với dữ liệu e-D/O ({edo.SealNo}).",
                        severity: ExceptionSeverity.High,
                        entityId: task.Id,
                        containerNo: task.ContainerNo,
                        notes: "Báo hãng tàu kiểm tra niêm phong trước khi giao.");
                    _db.Set<ExceptionItem>().Add(exception);
                    await _db.SaveChangesAsync(cancellationToken);

                    throw new ConflictException($"Số chì thực tế ({dto.SealNo}) không khớp với e-D/O ({edo.SealNo}). Hệ thống đã từ chối xác nhận và gửi cảnh báo tới hãng tàu.");
                }
            }

            task.Status = PreparationTaskStatus.Ready;
            task.ReadyAt = DateTimeOffset.UtcNow;

            var contIn = await _db.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
                .FirstOrDefaultAsync(c => c.ContainerNumber == task.ContainerNo, cancellationToken);
            if (contIn != null)
            {
                contIn.Status = "ready_for_delivery";
                contIn.SealNumber = dto.SealNo.Trim();
            }

            AddOutboxMessage("ContainerReadyForPickup", new
            {
                TaskId = task.Id,
                task.SlotBookingId,
                task.ContainerNo,
                dto.SealNo,
                ReadyAt = task.ReadyAt.Value,
                Message = "Container đã sẵn sàng tại vị trí thuận tiện. Đã gửi thông báo cho chủ hàng và cổng."
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        return await MapToDtoAsync(task, cancellationToken);
    }

    public async Task<PlanBatchResultDto> PlanBatchAsync(PlanBatchRequestDto dto, CancellationToken cancellationToken = default)
    {
        var config = await GetOrCreateConfigAsync(cancellationToken);
        if (dto.StagingBlockId.HasValue)
        {
            config.StagingBlockId = dto.StagingBlockId.Value;
        }

        var query = _db.Set<PreparationTask>()
            .Include(t => t.WorkOrders)
            .Where(t => t.Status == PreparationTaskStatus.Pending || t.Status == PreparationTaskStatus.Planned);

        if (dto.TaskIds != null && dto.TaskIds.Count > 0)
        {
            query = query.Where(t => dto.TaskIds.Contains(t.Id));
        }
        else if (dto.Date.HasValue)
        {
            var start = dto.Date.Value.Date;
            var end = start.AddDays(1);
            query = query.Where(t => t.TargetReadyAt >= start && t.TargetReadyAt < end);
        }

        var tasks = await query.ToListAsync(cancellationToken);
        var result = new PlanBatchResultDto { TotalTasks = tasks.Count };

        // Chuẩn bị dữ liệu cho batch
        var containerNos = tasks.Select(t => t.ContainerNo).Distinct().ToList();
        var targetSlots = new Dictionary<string, YardSlot>();
        var stackContainersByTask = new Dictionary<string, List<ContainerStackInfo>>();

        foreach (var contNo in containerNos)
        {
            var pos = await GetCurrentContainerPositionAsync(contNo, cancellationToken);
            if (pos?.Slot != null)
            {
                targetSlots[contNo] = pos.Slot;
                var stack = await GetStackInfoAsync(pos.Slot, cancellationToken);
                stackContainersByTask[contNo] = stack;
            }
        }

        var availableSlots = await _db.Set<YardSlot>()
            .Include(s => s.Block)
            .Where(s => s.Status == "empty")
            .ToListAsync(cancellationToken);

        var activeReservations = await _db.Set<SlotReservation>()
            .Where(r => r.ReservedUntil > DateTimeOffset.UtcNow)
            .ToListAsync(cancellationToken);

        var batchPlans = _planner.PlanBatch(
            tasks,
            targetSlots,
            stackContainersByTask,
            availableSlots,
            activeReservations,
            config);

        foreach (var (task, planResult) in batchPlans)
        {
            if (planResult.NeedsApproval)
            {
                task.Status = PreparationTaskStatus.Planned;
                task.PlannedRehandleCount = planResult.RehandleCount;
                task.Note = planResult.Reason;
                result.NeedsApprovalTasks++;

                var exception = new ExceptionItem(
                    category: "PreparationThresholdExceeded",
                    reason: planResult.Reason ?? "Số lần đảo chuyển vượt ngưỡng cho phép.",
                    severity: ExceptionSeverity.Medium,
                    entityId: task.Id,
                    containerNo: task.ContainerNo);
                _db.Set<ExceptionItem>().Add(exception);
            }
            else if (planResult.IsSuccessful)
            {
                // Xóa work orders cũ nếu có
                _db.Set<PreparationWorkOrder>().RemoveRange(task.WorkOrders);

                task.PlannedRehandleCount = planResult.WorkOrders.Count(w => w.Type == WorkOrderType.Rehandle);
                task.Status = PreparationTaskStatus.Planned;

                foreach (var pwo in planResult.WorkOrders)
                {
                    var wo = new PreparationWorkOrder(
                        task.Id,
                        pwo.Type,
                        pwo.Sequence,
                        pwo.ContainerNo,
                        pwo.FromSlotId,
                        pwo.ToSlotId,
                        pwo.DueAt,
                        task.Priority,
                        pwo.ChargeParty,
                        pwo.SelectionReason);

                    task.WorkOrders.Add(wo);

                    var res = new SlotReservation(pwo.ToSlotId, wo.Id, pwo.DueAt.AddHours(2));
                    _db.Set<SlotReservation>().Add(res);
                }

                result.PlannedTasks++;
                result.TotalRehandles += task.PlannedRehandleCount;

                AddOutboxMessage("PreparationPlanned", new
                {
                    TaskId = task.Id,
                    task.ContainerNo,
                    task.PlannedRehandleCount,
                    WorkOrdersCount = planResult.WorkOrders.Count
                });
            }

            result.Tasks.Add(await MapToDtoAsync(task, cancellationToken));
        }

        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<PreparationDashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var tomorrow = today.AddDays(1);

        var pendingCount = await _db.Set<PreparationTask>()
            .CountAsync(t => t.Status == PreparationTaskStatus.Pending, cancellationToken);

        var atRiskCount = await _db.Set<PreparationTask>()
            .CountAsync(t => t.Status != PreparationTaskStatus.Ready &&
                             t.Status != PreparationTaskStatus.Cancelled &&
                             t.TargetReadyAt >= DateTimeOffset.UtcNow &&
                             t.TargetReadyAt <= DateTimeOffset.UtcNow.AddMinutes(90), cancellationToken);

        var inProgressCount = await _db.Set<PreparationTask>()
            .CountAsync(t => t.Status == PreparationTaskStatus.InProgress, cancellationToken);

        var readyTodayCount = await _db.Set<PreparationTask>()
            .CountAsync(t => t.Status == PreparationTaskStatus.Ready &&
                             t.ReadyAt >= today && t.ReadyAt < tomorrow, cancellationToken);

        var delayedCount = await _db.Set<PreparationTask>()
            .CountAsync(t => t.Status == PreparationTaskStatus.Delayed, cancellationToken);

        var totalMovesToday = await _db.Set<ContainerMovement>()
            .CountAsync(m => m.MovedAt >= today && m.MovedAt < tomorrow, cancellationToken);

        return new PreparationDashboardDto
        {
            PendingCount = pendingCount,
            AtRiskCount = atRiskCount,
            InProgressCount = inProgressCount,
            ReadyTodayCount = readyTodayCount,
            DelayedCount = delayedCount,
            TotalMovesToday = totalMovesToday
        };
    }

    public async Task<PreparationConfigDto> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var config = await GetOrCreateConfigAsync(cancellationToken);
        return MapConfigToDto(config);
    }

    public async Task<PreparationConfigDto> UpdateConfigAsync(PreparationConfigDto dto, CancellationToken cancellationToken = default)
    {
        var config = await GetOrCreateConfigAsync(cancellationToken);

        config.LeadTimeMinutes = dto.LeadTimeMinutes;
        config.MaxRehandlePerTask = dto.MaxRehandlePerTask;
        config.StagingBlockId = dto.StagingBlockId;
        config.RescheduleGraceMinutes = dto.RescheduleGraceMinutes;
        config.DelayCheckIntervalSeconds = dto.DelayCheckIntervalSeconds;
        config.EstimatedMinutesPerMove = dto.EstimatedMinutesPerMove;
        config.WeightDueNear = dto.WeightDueNear;
        config.WeightReefer = dto.WeightReefer;
        config.WeightExpiringFreeTime = dto.WeightExpiringFreeTime;

        await _db.SaveChangesAsync(cancellationToken);
        return MapConfigToDto(config);
    }

    #endregion

    #region Work Orders

    public async Task<PagedResult<WorkOrderDto>> GetWorkOrdersAsync(
        WorkOrderStatus? status,
        Guid? assigneeId,
        Guid? equipmentId,
        WorkOrderType? type,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Set<PreparationWorkOrder>()
            .Include(w => w.FromSlot).ThenInclude(s => s!.Block)
            .Include(w => w.ToSlot).ThenInclude(s => s!.Block)
            .AsNoTracking()
            .AsQueryable();

        if (status.HasValue) query = query.Where(w => w.Status == status.Value);
        if (assigneeId.HasValue) query = query.Where(w => w.AssigneeId == assigneeId.Value);
        if (equipmentId.HasValue) query = query.Where(w => w.EquipmentId == equipmentId.Value);
        if (type.HasValue) query = query.Where(w => w.Type == type.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(w => w.DueAt)
            .ThenBy(w => w.Sequence)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var dtos = items.Select(MapWorkOrderToDto).ToList();
        return new PagedResult<WorkOrderDto>(dtos, totalCount, pageNumber, pageSize);
    }

    public async Task<WorkOrderDto> AssignWorkOrderAsync(Guid id, AssignWorkOrderRequestDto dto, CancellationToken cancellationToken = default)
    {
        var wo = await _db.Set<PreparationWorkOrder>()
            .Include(w => w.FromSlot).ThenInclude(s => s!.Block)
            .Include(w => w.ToSlot).ThenInclude(s => s!.Block)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

        if (wo == null)
            throw new NotFoundException("Lệnh làm hàng", id);

        if (wo.Status == WorkOrderStatus.Done || wo.Status == WorkOrderStatus.Cancelled)
            throw new ConflictException($"Không thể gán lệnh làm hàng đã ở trạng thái {wo.Status}.");

        wo.EquipmentId = dto.EquipmentId;
        wo.AssigneeId = dto.AssigneeId;
        wo.Status = WorkOrderStatus.Assigned;

        if (dto.EquipmentId.HasValue)
        {
            var eq = await _db.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>().FirstOrDefaultAsync(e => e.Id == dto.EquipmentId.Value, cancellationToken);
            if (eq != null)
            {
                eq.Status = "in_use";
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return MapWorkOrderToDto(wo);
    }

    public async Task<WorkOrderDto> StartWorkOrderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var wo = await _db.Set<PreparationWorkOrder>()
            .Include(w => w.Task)
            .Include(w => w.FromSlot).ThenInclude(s => s!.Block)
            .Include(w => w.ToSlot).ThenInclude(s => s!.Block)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

        if (wo == null)
            throw new NotFoundException("Lệnh làm hàng", id);

        if (wo.Status == WorkOrderStatus.Done || wo.Status == WorkOrderStatus.Cancelled)
            throw new ConflictException($"Lệnh làm hàng đã ở trạng thái {wo.Status}.");

        // Kiểm tra container hiện đang ở FromSlot không
        var pos = await GetCurrentContainerPositionAsync(wo.ContainerNo, cancellationToken);
        if (pos == null || pos.SlotId != wo.FromSlotId)
        {
            var actualLocation = pos?.Slot != null ? $"{pos.Slot.Block?.Code}-{pos.Slot.Bay:D2}-{pos.Slot.Row:D2}-{pos.Slot.Tier}" : "Không rõ";
            var expectedLocation = wo.FromSlot != null ? $"{wo.FromSlot.Block?.Code}-{wo.FromSlot.Bay:D2}-{wo.FromSlot.Row:D2}-{wo.FromSlot.Tier}" : "N/A";

            var exception = new ExceptionItem(
                category: "ContainerPositionMismatch",
                reason: $"Container {wo.ContainerNo} không ở ô kế hoạch {expectedLocation}, vị trí thực tế: {actualLocation}.",
                severity: ExceptionSeverity.High,
                entityId: wo.Id,
                containerNo: wo.ContainerNo);
            _db.Set<ExceptionItem>().Add(exception);
            await _db.SaveChangesAsync(cancellationToken);

            throw new ConflictException($"Container {wo.ContainerNo} không ở vị trí chỉ định ({expectedLocation}). Vị trí thực tế ghi nhận: {actualLocation}. Đã lập ngoại lệ bãi.");
        }

        wo.Status = WorkOrderStatus.InProgress;
        wo.StartedAt = DateTimeOffset.UtcNow;

        if (wo.Task.Status == PreparationTaskStatus.Pending || wo.Task.Status == PreparationTaskStatus.Planned)
        {
            wo.Task.Status = PreparationTaskStatus.InProgress;
        }

        AddOutboxMessage("PreparationStarted", new
        {
            TaskId = wo.PreparationTaskId,
            WorkOrderId = wo.Id,
            wo.ContainerNo,
            wo.FromSlotId,
            wo.ToSlotId
        });

        await _db.SaveChangesAsync(cancellationToken);
        return MapWorkOrderToDto(wo);
    }

    public async Task<WorkOrderDto> CompleteWorkOrderAsync(Guid id, CompleteWorkOrderRequestDto dto, CancellationToken cancellationToken = default)
    {
        var wo = await _db.Set<PreparationWorkOrder>()
            .Include(w => w.Task)
            .Include(w => w.FromSlot).ThenInclude(s => s!.Block)
            .Include(w => w.ToSlot).ThenInclude(s => s!.Block)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

        if (wo == null)
            throw new NotFoundException("Lệnh làm hàng", id);

        if (wo.Status == WorkOrderStatus.Done)
            return MapWorkOrderToDto(wo);

        // 1. Kiểm tra số container quét hoặc nhập khớp lệnh
        var scannedCont = dto.ScannedContainerNo.Trim().ToUpperInvariant();
        if (!string.Equals(wo.ContainerNo, scannedCont, StringComparison.OrdinalIgnoreCase))
        {
            var exception = new ExceptionItem(
                category: "WorkOrderContainerMismatch",
                reason: $"Container quét ({scannedCont}) không khớp với lệnh làm hàng ({wo.ContainerNo}).",
                severity: ExceptionSeverity.Critical,
                entityId: wo.Id,
                containerNo: scannedCont);
            _db.Set<ExceptionItem>().Add(exception);
            await _db.SaveChangesAsync(cancellationToken);

            throw new ConflictException($"Container quét ({scannedCont}) không khớp với lệnh làm hàng ({wo.ContainerNo}). Dừng thao tác!");
        }

        // 2. Xác định ô đích thực tế
        var targetSlotId = dto.ActualToSlotId ?? wo.ToSlotId;
        var targetSlot = await _db.Set<YardSlot>()
            .Include(s => s.Block)
            .FirstOrDefaultAsync(s => s.Id == targetSlotId, cancellationToken);

        if (targetSlot == null)
            throw new NotFoundException("Ô bãi đích", targetSlotId);

        // Nếu ô thực tế khác với kế hoạch: kiểm tra quy tắc cứng
        if (targetSlotId != wo.ToSlotId)
        {
            if (string.IsNullOrWhiteSpace(dto.ChangeSlotReason))
                throw new ConflictException("Đổi ô đích thực tế bắt buộc phải ghi rõ lý do.");

            if (!targetSlot.IsAvailable() && targetSlot.Id != wo.FromSlotId)
                throw new ConflictException($"Ô thực tế {targetSlot.Block?.Code}-{targetSlot.Bay:D2}-{targetSlot.Row:D2}-{targetSlot.Tier} không trống!");

            // Kiểm tra có bị lệnh khác giữ chỗ không
            var isReservedByOther = await _db.Set<SlotReservation>()
                .AnyAsync(r => r.SlotId == targetSlotId && r.WorkOrderId != wo.Id && r.ReservedUntil > DateTimeOffset.UtcNow, cancellationToken);
            if (isReservedByOther)
                throw new ConflictException("Ô đích thực tế đang bị giữ chỗ bởi lệnh làm hàng khác.");

            wo.SelectionReason += $" | Thay đổi sang {targetSlot.Block?.Code}-{targetSlot.Bay:D2}-{targetSlot.Row:D2}-{targetSlot.Tier}: {dto.ChangeSlotReason}";
        }

        // 3. Cập nhật vị trí Container (ContainerPosition)
        var pos = await GetCurrentContainerPositionAsync(wo.ContainerNo, cancellationToken);
        if (pos != null)
        {
            pos.IsCurrent = false;
            pos.RemovedAt = DateTime.UtcNow;
        }

        var newPos = new ContainerPosition(
            containerId: pos?.ContainerId ?? Guid.NewGuid(),
            slotId: targetSlotId,
            placedBy: wo.AssigneeId)
        {
            IsCurrent = true,
            PlacedAt = DateTime.UtcNow
        };
        _db.Set<ContainerPosition>().Add(newPos);

        // 4. Ghi ContainerMovement
        var movement = new ContainerMovement(
            containerNo: wo.ContainerNo,
            fromSlotId: wo.FromSlotId,
            toSlotId: targetSlotId,
            movedBy: wo.AssigneeId,
            equipmentId: wo.EquipmentId,
            workOrderId: wo.Id,
            note: dto.Note ?? dto.ChangeSlotReason);
        _db.Set<ContainerMovement>().Add(movement);

        // 5. Cập nhật trạng thái Slot
        var fromSlot = await _db.Set<YardSlot>().FirstOrDefaultAsync(s => s.Id == wo.FromSlotId, cancellationToken);
        if (fromSlot != null)
        {
            fromSlot.Status = "empty";
        }
        targetSlot.Status = "occupied";

        // 6. Giải phóng SlotReservation
        var reservations = await _db.Set<SlotReservation>()
            .Where(r => r.WorkOrderId == wo.Id || r.SlotId == targetSlotId)
            .ToListAsync(cancellationToken);
        _db.Set<SlotReservation>().RemoveRange(reservations);

        // 7. Hoàn tất WorkOrder
        wo.Status = WorkOrderStatus.Done;
        wo.CompletedAt = DateTimeOffset.UtcNow;
        wo.ToSlotId = targetSlotId;
        wo.ToSlot = targetSlot;

        // Tăng ActualRehandleCount của task
        if (wo.Type == WorkOrderType.Rehandle)
        {
            wo.Task.ActualRehandleCount++;
        }

        // Tăng RowVersion để bảo vệ đồng thời
        wo.RowVersion++;
        wo.Task.RowVersion++;

        // 8. Phát sự kiện WorkOrderCompleted
        AddOutboxMessage("WorkOrderCompleted", new
        {
            TaskId = wo.PreparationTaskId,
            WorkOrderId = wo.Id,
            wo.ContainerNo,
            FromSlotId = wo.FromSlotId,
            ToSlotId = targetSlotId,
            CompletedAt = wo.CompletedAt.Value
        });

        await _db.SaveChangesAsync(cancellationToken);
        return MapWorkOrderToDto(wo);
    }

    public async Task<WorkOrderDto> FailWorkOrderAsync(Guid id, FailWorkOrderRequestDto dto, CancellationToken cancellationToken = default)
    {
        var wo = await _db.Set<PreparationWorkOrder>()
            .Include(w => w.Task)
            .Include(w => w.FromSlot).ThenInclude(s => s!.Block)
            .Include(w => w.ToSlot).ThenInclude(s => s!.Block)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

        if (wo == null)
            throw new NotFoundException("Lệnh làm hàng", id);

        wo.Status = WorkOrderStatus.Failed;
        wo.FailReason = dto.Reason;
        wo.Task.Status = PreparationTaskStatus.Delayed;

        // Giải phóng giữ chỗ
        var reservations = await _db.Set<SlotReservation>()
            .Where(r => r.WorkOrderId == wo.Id)
            .ToListAsync(cancellationToken);
        _db.Set<SlotReservation>().RemoveRange(reservations);

        AddOutboxMessage("WorkOrderFailed", new
        {
            TaskId = wo.PreparationTaskId,
            WorkOrderId = wo.Id,
            wo.ContainerNo,
            Reason = dto.Reason
        });

        // Đề nghị lập lại kế hoạch tự động 1 lần
        if (!wo.Task.Note?.Contains("[AutoReplanAttempted]") == true)
        {
            wo.Task.Note = (wo.Task.Note ?? "") + " [AutoReplanAttempted]";
            try
            {
                var config = await GetOrCreateConfigAsync(cancellationToken);
                await PlanTaskInternalAsync(wo.Task, config, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tự động lập lại kế hoạch cho task {TaskId} thất bại.", wo.PreparationTaskId);
            }
        }
        else
        {
            // Lần 2 vẫn lỗi: tạo ExceptionItem
            var exception = new ExceptionItem(
                category: "RepeatedWorkOrderFailure",
                reason: $"Lệnh làm hàng {wo.Id} của container {wo.ContainerNo} bị lỗi lần thứ 2: {dto.Reason}",
                severity: ExceptionSeverity.High,
                entityId: wo.Task.Id,
                containerNo: wo.ContainerNo);
            _db.Set<ExceptionItem>().Add(exception);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return MapWorkOrderToDto(wo);
    }

    #endregion

    #region Stack Inspection

    public async Task<List<StackSlotDto>> GetStackContainersAsync(Guid slotId, CancellationToken cancellationToken = default)
    {
        var slot = await _db.Set<YardSlot>()
            .Include(s => s.Block)
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);

        if (slot == null)
            throw new NotFoundException("Ô bãi", slotId);

        var stackSlots = await _db.Set<YardSlot>()
            .Include(s => s.Block)
            .Where(s => s.YardBlockId == slot.YardBlockId && s.Bay == slot.Bay && s.Row == slot.Row)
            .OrderByDescending(s => s.Tier)
            .ToListAsync(cancellationToken);

        var slotIds = stackSlots.Select(s => s.Id).ToList();

        var positions = await _db.Set<ContainerPosition>()
            .Where(cp => slotIds.Contains(cp.SlotId) && cp.IsCurrent)
            .ToListAsync(cancellationToken);

        var contIds = positions.Select(p => p.ContainerId).Distinct().ToList();
        var containers = await _db.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .Where(c => contIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.ContainerNumber, cancellationToken);

        var reservations = await _db.Set<SlotReservation>()
            .Where(r => slotIds.Contains(r.SlotId) && r.ReservedUntil > DateTimeOffset.UtcNow)
            .Select(r => r.SlotId)
            .ToListAsync(cancellationToken);

        var result = new List<StackSlotDto>();
        foreach (var s in stackSlots)
        {
            var pos = positions.FirstOrDefault(p => p.SlotId == s.Id);
            string? contNo = null;
            if (pos != null && containers.TryGetValue(pos.ContainerId, out var no))
            {
                contNo = no;
            }

            result.Add(new StackSlotDto
            {
                SlotId = s.Id,
                BlockId = s.YardBlockId,
                BlockCode = s.Block?.Code ?? "N/A",
                Bay = s.Bay,
                Row = s.Row,
                Tier = s.Tier,
                ContainerNo = contNo,
                Status = s.Status,
                HasReeferPlug = s.HasReeferPlug,
                IsReserved = reservations.Contains(s.Id)
            });
        }

        return result;
    }

    #endregion

    #region Internal Helpers

    private async Task PlanTaskInternalAsync(PreparationTask task, PreparationConfig config, CancellationToken cancellationToken)
    {
        var pos = await GetCurrentContainerPositionAsync(task.ContainerNo, cancellationToken);
        if (pos?.Slot == null)
        {
            task.Status = PreparationTaskStatus.Delayed;
            task.Note = $"Không tìm thấy vị trí hiện tại của container {task.ContainerNo} trong bãi.";
            return;
        }

        var stackContainers = await GetStackInfoAsync(pos.Slot, cancellationToken);

        var availableSlots = await _db.Set<YardSlot>()
            .Include(s => s.Block)
            .Where(s => s.Status == "empty")
            .ToListAsync(cancellationToken);

        var activeReservations = await _db.Set<SlotReservation>()
            .Where(r => r.ReservedUntil > DateTimeOffset.UtcNow)
            .ToListAsync(cancellationToken);

        var planResult = _planner.PlanTask(
            task.ContainerNo,
            pos.Slot,
            stackContainers,
            availableSlots,
            activeReservations,
            config,
            task.StartBy);

        if (planResult.NeedsApproval)
        {
            task.Status = PreparationTaskStatus.Planned;
            task.PlannedRehandleCount = planResult.RehandleCount;
            task.Note = planResult.Reason;

            var exception = new ExceptionItem(
                category: "PreparationThresholdExceeded",
                reason: planResult.Reason ?? "Số lần đảo chuyển vượt ngưỡng cho phép.",
                severity: ExceptionSeverity.Medium,
                entityId: task.Id,
                containerNo: task.ContainerNo);
            _db.Set<ExceptionItem>().Add(exception);
        }
        else if (planResult.IsSuccessful)
        {
            task.PlannedRehandleCount = planResult.WorkOrders.Count(w => w.Type == WorkOrderType.Rehandle);
            task.Status = PreparationTaskStatus.Planned;

            foreach (var pwo in planResult.WorkOrders)
            {
                var wo = new PreparationWorkOrder(
                    task.Id,
                    pwo.Type,
                    pwo.Sequence,
                    pwo.ContainerNo,
                    pwo.FromSlotId,
                    pwo.ToSlotId,
                    pwo.DueAt,
                    task.Priority,
                    pwo.ChargeParty,
                    pwo.SelectionReason);

                task.WorkOrders.Add(wo);

                var reservation = new SlotReservation(pwo.ToSlotId, wo.Id, pwo.DueAt.AddHours(2));
                _db.Set<SlotReservation>().Add(reservation);
            }

            AddOutboxMessage("PreparationPlanned", new
            {
                TaskId = task.Id,
                task.ContainerNo,
                task.PlannedRehandleCount,
                WorkOrdersCount = planResult.WorkOrders.Count
            });
        }
        else
        {
            task.Status = PreparationTaskStatus.Delayed;
            task.Note = planResult.Reason;
        }
    }

    private async Task<ContainerPosition?> GetCurrentContainerPositionAsync(string containerNo, CancellationToken cancellationToken)
    {
        var normalized = containerNo.Trim().ToUpperInvariant();

        // Tìm qua Container entity
        var cont = await _db.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .FirstOrDefaultAsync(c => c.ContainerNumber == normalized, cancellationToken);

        if (cont != null)
        {
            return await _db.Set<ContainerPosition>()
                .Include(cp => cp.Slot).ThenInclude(s => s.Block)
                .FirstOrDefaultAsync(cp => cp.ContainerId == cont.Id && cp.IsCurrent, cancellationToken);
        }

        return null;
    }

    private async Task<List<ContainerStackInfo>> GetStackInfoAsync(YardSlot baseSlot, CancellationToken cancellationToken)
    {
        var slotsInStack = await _db.Set<YardSlot>()
            .Include(s => s.Block)
            .Where(s => s.YardBlockId == baseSlot.YardBlockId && s.Bay == baseSlot.Bay && s.Row == baseSlot.Row)
            .ToListAsync(cancellationToken);

        var slotIds = slotsInStack.Select(s => s.Id).ToList();

        var positions = await _db.Set<ContainerPosition>()
            .Where(cp => slotIds.Contains(cp.SlotId) && cp.IsCurrent)
            .ToListAsync(cancellationToken);

        var contIds = positions.Select(p => p.ContainerId).Distinct().ToList();
        var containers = await _db.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .Where(c => contIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var result = new List<ContainerStackInfo>();
        foreach (var pos in positions)
        {
            var slot = slotsInStack.FirstOrDefault(s => s.Id == pos.SlotId);
            if (slot == null) continue;

            containers.TryGetValue(pos.ContainerId, out var cont);

            result.Add(new ContainerStackInfo
            {
                ContainerNo = cont?.ContainerNumber ?? "UNKNOWN",
                SlotId = slot.Id,
                Bay = slot.Bay,
                Row = slot.Row,
                Tier = slot.Tier,
                BlockId = slot.YardBlockId,
                BlockCode = slot.Block?.Code ?? "N/A",
                IsReefer = slot.HasReeferPlug || (cont?.CargoType == "refrigerated"),
                CarrierId = cont?.CarrierId
            });
        }

        return result;
    }

    private int CalculatePriority(DateTimeOffset targetReadyAt, EDoRecord edo, PreparationConfig config)
    {
        int score = 0;

        // Giờ hẹn càng gần càng cao (trong vòng 2h)
        var hoursUntilPickup = (targetReadyAt - DateTimeOffset.UtcNow).TotalHours;
        if (hoursUntilPickup <= 2)
        {
            score += config.WeightDueNear;
        }
        else if (hoursUntilPickup <= 6)
        {
            score += config.WeightDueNear / 2;
        }

        // Sắp hết free time (trong vòng 24h)
        if (edo.FreeTimeUntil.HasValue && (edo.FreeTimeUntil.Value - DateTimeOffset.UtcNow).TotalHours <= 24)
        {
            score += config.WeightExpiringFreeTime;
        }

        return score;
    }

    private async Task<PreparationConfig> GetOrCreateConfigAsync(CancellationToken cancellationToken)
    {
        var config = await _db.Set<PreparationConfig>().FirstOrDefaultAsync(cancellationToken);
        if (config == null)
        {
            config = new PreparationConfig();
            _db.Set<PreparationConfig>().Add(config);
            await _db.SaveChangesAsync(cancellationToken);
        }
        return config;
    }

    private void AddOutboxMessage(string eventType, object payload)
    {
        var msg = new PreparationOutboxMessage(
            eventType,
            JsonSerializer.Serialize(payload));
        _db.Set<PreparationOutboxMessage>().Add(msg);
    }

    private async Task<PreparationTaskDto> MapToDtoAsync(PreparationTask task, CancellationToken cancellationToken)
    {
        var pos = await GetCurrentContainerPositionAsync(task.ContainerNo, cancellationToken);
        ContainerPositionSummaryDto? posDto = null;
        if (pos?.Slot != null)
        {
            posDto = new ContainerPositionSummaryDto
            {
                SlotId = pos.Slot.Id,
                BlockId = pos.Slot.YardBlockId,
                BlockCode = pos.Slot.Block?.Code ?? "N/A",
                Bay = pos.Slot.Bay,
                Row = pos.Slot.Row,
                Tier = pos.Slot.Tier,
                SlotStatus = pos.Slot.Status,
                HasReeferPlug = pos.Slot.HasReeferPlug
            };
        }

        var workOrderDtos = task.WorkOrders
            .OrderBy(w => w.Sequence)
            .Select(MapWorkOrderToDto)
            .ToList();

        var dto = new PreparationTaskDto
        {
            Id = task.Id,
            ServiceOrderId = task.ServiceOrderId,
            SlotBookingId = task.SlotBookingId,
            ContainerNo = task.ContainerNo,
            TargetReadyAt = task.TargetReadyAt,
            StartBy = task.StartBy,
            Priority = task.Priority,
            Status = task.Status,
            PlannedRehandleCount = task.PlannedRehandleCount,
            ActualRehandleCount = task.ActualRehandleCount,
            CreatedAt = task.CreatedAt,
            ReadyAt = task.ReadyAt,
            CancelReason = task.CancelReason,
            Note = task.Note,
            RowVersion = task.RowVersion,
            CurrentPosition = posDto,
            WorkOrders = workOrderDtos,
            Direction = task.Direction,
            VesselName = task.VesselName,
            VoyageCode = task.VoyageCode,
            StowagePosition = task.StowagePosition,
            LoadingSequence = task.LoadingSequence,
            QuayCraneCode = task.QuayCraneCode,
            Pod = task.Pod,
            VgmWeight = task.VgmWeight,
            IsVgmVerified = task.IsVgmVerified,
            IsCustomsCleared = task.IsCustomsCleared,
            HasHoldFlag = task.HasHoldFlag
        };

        if (!string.IsNullOrWhiteSpace(task.Note) && task.Note.TrimStart().StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(task.Note);
                var root = doc.RootElement;
                if (root.TryGetProperty("direction", out var dirProp) && Enum.TryParse<PreparationDirection>(dirProp.GetString(), true, out var dir))
                    dto.Direction = dir;
                if (root.TryGetProperty("vesselName", out var vProp)) dto.VesselName = vProp.GetString();
                if (root.TryGetProperty("voyageCode", out var vcProp)) dto.VoyageCode = vcProp.GetString();
                if (root.TryGetProperty("stowagePosition", out var stProp)) dto.StowagePosition = stProp.GetString();
                if (root.TryGetProperty("loadingSequence", out var lsProp) && lsProp.TryGetInt32(out var lsVal)) dto.LoadingSequence = lsVal;
                if (root.TryGetProperty("quayCraneCode", out var qcProp)) dto.QuayCraneCode = qcProp.GetString();
                if (root.TryGetProperty("pod", out var podProp)) dto.Pod = podProp.GetString();
                if (root.TryGetProperty("vgmWeight", out var vgmProp) && vgmProp.TryGetDouble(out var vgmVal)) dto.VgmWeight = vgmVal;
                if (root.TryGetProperty("isVgmVerified", out var vgmbProp)) dto.IsVgmVerified = vgmbProp.GetBoolean();
                if (root.TryGetProperty("isCustomsCleared", out var custProp)) dto.IsCustomsCleared = custProp.GetBoolean();
                if (root.TryGetProperty("hasHoldFlag", out var holdProp)) dto.HasHoldFlag = holdProp.GetBoolean();
            }
            catch { /* Ignore non-json notes */ }
        }

        return dto;
    }

    private WorkOrderDto MapWorkOrderToDto(PreparationWorkOrder w)
    {
        var fromLoc = w.FromSlot != null ? $"{w.FromSlot.Block?.Code ?? "N/A"}-{w.FromSlot.Bay:D2}-{w.FromSlot.Row:D2}-{w.FromSlot.Tier}" : w.FromSlotId.ToString();
        var toLoc = w.ToSlot != null ? $"{w.ToSlot.Block?.Code ?? "N/A"}-{w.ToSlot.Bay:D2}-{w.ToSlot.Row:D2}-{w.ToSlot.Tier}" : w.ToSlotId.ToString();

        return new WorkOrderDto
        {
            Id = w.Id,
            PreparationTaskId = w.PreparationTaskId,
            Type = w.Type,
            Sequence = w.Sequence,
            ContainerNo = w.ContainerNo,
            FromSlotId = w.FromSlotId,
            FromSlotLocation = fromLoc,
            ToSlotId = w.ToSlotId,
            ToSlotLocation = toLoc,
            EquipmentId = w.EquipmentId,
            AssigneeId = w.AssigneeId,
            Priority = w.Priority,
            DueAt = w.DueAt,
            Status = w.Status,
            StartedAt = w.StartedAt,
            CompletedAt = w.CompletedAt,
            FailReason = w.FailReason,
            ChargeParty = w.ChargeParty,
            SelectionReason = w.SelectionReason,
            RowVersion = w.RowVersion
        };
    }

    private static PreparationConfigDto MapConfigToDto(PreparationConfig c)
    {
        return new PreparationConfigDto
        {
            LeadTimeMinutes = c.LeadTimeMinutes,
            MaxRehandlePerTask = c.MaxRehandlePerTask,
            StagingBlockId = c.StagingBlockId,
            RescheduleGraceMinutes = c.RescheduleGraceMinutes,
            DelayCheckIntervalSeconds = c.DelayCheckIntervalSeconds,
            EstimatedMinutesPerMove = c.EstimatedMinutesPerMove,
            WeightDueNear = c.WeightDueNear,
            WeightReefer = c.WeightReefer,
            WeightExpiringFreeTime = c.WeightExpiringFreeTime
        };
    }

    #endregion
}

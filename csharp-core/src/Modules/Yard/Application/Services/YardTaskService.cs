using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.ExternalServices;
using NexusPort.Infrastructure.Notifications.DTOs;
using NexusPort.Infrastructure.Notifications.Enums;
using NexusPort.Infrastructure.Notifications.Interfaces;
using NexusPort.Modules.Container.Domain.Entities;
using NexusPort.Modules.Equipment.Domain.Entities;
using NexusPort.Modules.Identity.Domain.Entities;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;
using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Yard.Application.Services;

public class YardTaskService : IYardTaskService
{
    private readonly AppDbContext _context;
    private readonly IMessageBrokerService _messageBroker;
    private readonly INotificationService _notificationService;
    private readonly ILogger<YardTaskService> _logger;
    private readonly IS3StorageService _s3;
    private static bool _tableEnsured = false;

    public YardTaskService(
        AppDbContext context,
        IMessageBrokerService messageBroker,
        INotificationService notificationService,
        ILogger<YardTaskService> logger,
        IS3StorageService s3)
    {
        _s3 = s3;
        _context = context;
        _messageBroker = messageBroker;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<YardTaskDto>> GetAllAsync(string? blockCode, string? status, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        var query = _context.Set<YardTask>().AsQueryable();

        if (!string.IsNullOrWhiteSpace(blockCode))
        {
            query = query.Where(t => t.BlockCode.ToLower() == blockCode.ToLower());
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(t => t.Status.ToLower() == status.ToLower());
        }

        var tasks = await query.OrderByDescending(t => t.CreatedAt).ToListAsync(cancellationToken);
        return tasks.Select(MapToDto).ToList();
    }

    public async Task<YardTaskDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);
        var task = await _context.Set<YardTask>().FindAsync(new object[] { id }, cancellationToken);
        return task == null ? null : MapToDto(task);
    }

    public async Task<YardTaskDto> AssignEquipmentAsync(Guid taskId, AssignEquipmentRequestDto dto, string assignedBy, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        // 1. Kiểm tra tồn tại của Task
        var task = await _context.Set<YardTask>().FindAsync(new object[] { taskId }, cancellationToken);
        if (task == null)
        {
            throw new NotFoundException($"Không tìm thấy lệnh tác nghiệp bãi với mã ID: {taskId}");
        }

        // 2. Lấy thông tin Thiết bị
        var equipment = await _context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>()
            .FindAsync(new object[] { dto.EquipmentId }, cancellationToken);

        if (equipment == null)
        {
            throw new NotFoundException("Không tìm thấy thiết bị nâng hạ được chọn.");
        }

        // 🛑 Rule 1 - Conflict Check Cẩu: Cẩu phải Available. Nếu Busy hoặc Maintenance -> 422
        var eqStatus = equipment.Status.ToLower().Trim();
        if (eqStatus == "busy" || eqStatus == "working" || eqStatus == "in_use" || eqStatus == "maintenance" || eqStatus == "inactive")
        {
            throw new ValidationException("EquipmentId",
                $"❌ Lỗi Rule 1: Thiết bị [{equipment.EquipmentCode} - {equipment.Name}] hiện đang bận hoặc bảo trì (Trạng thái: {equipment.Status})!");
        }

        // 🛑 Rule 2 - Conflict Check Cần thủ: Cần thủ không được đang làm Task khác ở trạng thái In_Progress
        var busyTaskForOperator = await _context.Set<YardTask>()
            .FirstOrDefaultAsync(t => t.OperatorId == dto.OperatorId && t.Id != taskId && t.Status.ToLower() == "in_progress", cancellationToken);

        if (busyTaskForOperator != null)
        {
            throw new ValidationException("OperatorId",
                $"❌ Lỗi Rule 2: Cần thủ [{dto.OperatorName ?? "được chọn"}] đang vận hành một nhiệm vụ khác ({busyTaskForOperator.TaskCode}) ở trạng thái Đang thực hiện!");
        }

        // 🛑 Rule 3 - Zone Matching: Cẩu đỗ tại Block A01 chỉ được nhận Task thuộc Block A01
        if (!string.IsNullOrWhiteSpace(equipment.BlockCode) &&
            !string.IsNullOrWhiteSpace(task.BlockCode) &&
            !string.Equals(equipment.BlockCode.Trim(), task.BlockCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("BlockCode",
                $"❌ Lỗi Rule 3 (Zone Mismatch): Thiết bị [{equipment.EquipmentCode}] đang đỗ tại Block {equipment.BlockCode}, không thể điều phối sang Task tại Block {task.BlockCode}!");
        }

        // ✅ Xử lý khi thành công: Cập nhật Task
        task.EquipmentId = equipment.Id;
        task.EquipmentCode = equipment.EquipmentCode;
        task.EquipmentType = equipment.EquipmentType;
        task.OperatorId = dto.OperatorId;
        task.OperatorName = string.IsNullOrWhiteSpace(dto.OperatorName) ? "Phạm Bãi Hàng" : dto.OperatorName;
        task.AssignedBy = string.IsNullOrWhiteSpace(assignedBy) ? "Yard Staff" : assignedBy;
        task.AssignedAt = DateTime.UtcNow;
        task.Status = "Ready"; // Chuyển từ Assigned -> Ready (Ready for Operation)
        if (!string.IsNullOrWhiteSpace(dto.Notes))
        {
            task.Notes = dto.Notes;
        }

        // Cập nhật trạng thái Thiết bị sang Working
        equipment.Status = "working";
        equipment.OperatorId = dto.OperatorId;
        equipment.OperatorName = task.OperatorName;

        await _context.SaveChangesAsync(cancellationToken);

        // Phát Real-time Event
        try
        {
            await _messageBroker.PublishAsync("dispatcher.status.updated", new DispatcherStatusUpdatedEvent(
                Guid.NewGuid(), "yard_task", task.Id.ToString(), "Ready", DateTime.UtcNow,
                Label: $"Task {task.TaskCode} đã gán {equipment.EquipmentCode} cho {task.OperatorName}"), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish realtime event for yard task {TaskId}", task.Id);
        }

        return MapToDto(task);
    }

    public async Task<YardTaskDto> StartLiftAsync(Guid taskId, StartLiftDto dto, string operatorName, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        var task = await _context.Set<YardTask>().FindAsync(new object[] { taskId }, cancellationToken);
        if (task == null)
        {
            throw new NotFoundException($"Không tìm thấy nhiệm vụ tác nghiệp bãi với mã ID: {taskId}");
        }

        if (task.Status.ToLower() == "in_progress")
        {
            throw new ValidationException("Status", $"Nhiệm vụ {task.TaskCode} đã ở trạng thái Đang cẩu (In_Progress).");
        }

        if (task.Status.ToLower() == "completed")
        {
            throw new ValidationException("Status", $"Nhiệm vụ {task.TaskCode} đã hoàn thành, không thể bắt đầu lại.");
        }

        // Task Status: Ready/Assigned -> In_Progress
        task.Status = "In_Progress";
        task.StartTime = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(dto.Notes))
        {
            task.Notes = string.IsNullOrWhiteSpace(task.Notes) ? dto.Notes : $"{task.Notes} | {dto.Notes}";
        }

        // Equipment Status: Available/Working -> Busy (working)
        if (task.EquipmentId != null)
        {
            var equipment = await _context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>()
                .FindAsync(new object[] { task.EquipmentId.Value }, cancellationToken);
            if (equipment != null)
            {
                equipment.Status = "working";
            }
        }

        // Container Status: In_Transit -> Handling
        var container = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == task.ContainerNo.ToUpper(), cancellationToken);
        if (container != null)
        {
            container.Status = "moving"; // nhãn "Handling" không tồn tại trong enum container_status của database
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Publish Realtime Event
        try
        {
            await _messageBroker.PublishAsync("dispatcher.status.updated", new DispatcherStatusUpdatedEvent(
                Guid.NewGuid(), "yard_task", task.Id.ToString(), "In_Progress", DateTime.UtcNow,
                Label: $"Bắt đầu cẩu container {task.ContainerNo} - Cẩu {task.EquipmentCode ?? "RTG"}"), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish realtime start-lift event for {TaskId}", task.Id);
        }

        return MapToDto(task);
    }

    public async Task<YardTaskDto> CompleteLiftAsync(Guid taskId, CompleteLiftDto dto, string operatorName, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        var task = await _context.Set<YardTask>().FindAsync(new object[] { taskId }, cancellationToken);
        if (task == null)
        {
            throw new NotFoundException($"Không tìm thấy nhiệm vụ tác nghiệp bãi với mã ID: {taskId}");
        }

        if (await _context.Set<YardRestackPlanStep>().AnyAsync(s => s.YardTaskId == taskId, cancellationToken))
            throw new ValidationException("TaskId", "Lệnh thuộc kế hoạch đảo chuyển; xác nhận theo bước của kế hoạch để đồng bộ vị trí và mở bước kế tiếp.");

        if (task.Status.ToLower() != "in_progress")
        {
            throw new ValidationException("Status", $"Nhiệm vụ {task.TaskCode} không ở trạng thái Đang cẩu (Hiện tại: {task.Status})!");
        }

        await using var completionTransaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var isRelocation = string.Equals(task.OperationType, "Relocate", StringComparison.OrdinalIgnoreCase);
        if (isRelocation)
        {
            var currentContainer = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
                .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == task.ContainerNo.ToUpper(), cancellationToken);
            var currentPosition = currentContainer == null ? null : await _context.Set<ContainerPosition>()
                .Include(p => p.Slot).ThenInclude(s => s.Block)
                .SingleOrDefaultAsync(p => p.ContainerId == currentContainer.Id && p.IsCurrent, cancellationToken);
            if (currentPosition == null || !string.Equals(LiveRestackAnalyzer.Location(currentPosition.Slot),
                    task.FromLocation, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("FromLocation", "Container không còn ở ô nguồn của lệnh di chuyển.");
            if (await _context.Set<ContainerPosition>().AnyAsync(p => p.IsCurrent &&
                p.Slot.YardBlockId == currentPosition.Slot.YardBlockId &&
                p.Slot.Bay == currentPosition.Slot.Bay && p.Slot.Row == currentPosition.Slot.Row &&
                p.Slot.Tier > currentPosition.Slot.Tier, cancellationToken))
                throw new ValidationException("Stack", "Container còn bị đè; cần xử lý các container phía trên trước.");
            if (string.IsNullOrWhiteSpace(task.ToLocation) ||
                !string.IsNullOrWhiteSpace(dto.CompletedLocation) &&
                !string.Equals(dto.CompletedLocation, task.ToLocation, StringComparison.OrdinalIgnoreCase))
                throw new ValidationException("CompletedLocation", "Vị trí hoàn tất phải khớp ô đích đã được duyệt.");
            var (toBlockCode, toBay, toRow, toTier) = ParseSlotLocation(task.ToLocation);
            var destination = await _context.Set<YardSlot>().Include(s => s.Block)
                .AsNoTracking().FirstOrDefaultAsync(s =>
                    s.Block.Code == toBlockCode && s.Bay == toBay &&
                    s.Row == toRow && s.Tier == toTier, cancellationToken);
            if (destination == null || destination.Status != "empty" ||
                await _context.Set<ContainerPosition>().AnyAsync(p => p.IsCurrent && p.SlotId == destination.Id, cancellationToken))
                throw new ValidationException("ToLocation", "Ô đích không còn trống; cần lập lại lệnh.");
        }

        // Task Status: In_Progress -> Completed
        task.Status = "Completed";
        task.EndTime = DateTime.UtcNow;
        task.CompletedLocation = !string.IsNullOrWhiteSpace(dto.CompletedLocation) ? dto.CompletedLocation : task.ToLocation;
        if (!string.IsNullOrWhiteSpace(dto.Notes))
        {
            task.Notes = string.IsNullOrWhiteSpace(task.Notes) ? dto.Notes : $"{task.Notes} | {dto.Notes}";
        }

        // Equipment Status: Busy -> Available (Giải phóng thiết bị)
        if (task.EquipmentId != null)
        {
            var equipment = await _context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>()
                .FindAsync(new object[] { task.EquipmentId.Value }, cancellationToken);
            if (equipment != null)
            {
                equipment.Status = "available";
            }
        }

        // Container Status: Stacked & Update location
        var container = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == task.ContainerNo.ToUpper(), cancellationToken);
        if (container != null)
        {
            container.Status = "Stacked";
            container.Description = $"Lưu tại bãi {task.BlockCode} vị trí {task.CompletedLocation}";

            // Cập nhật vị trí bãi yard_slots & container_positions
            try
            {
                // Giải phóng slot cũ
                var currentPos = await _context.Set<ContainerPosition>()
                    .FirstOrDefaultAsync(cp => cp.ContainerId == container.Id && cp.IsCurrent, cancellationToken);
                if (currentPos != null)
                {
                    currentPos.IsCurrent = false;
                    currentPos.RemovedAt = DateTime.UtcNow;
                    await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE yard_slots SET status = 'empty'::yard_slot_status WHERE id = {currentPos.SlotId}", cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(task.FromLocation))
                {
                    var (fromBlockCode, fromBay, fromRow, fromTier) = ParseSlotLocation(task.FromLocation);
                    var fromBlock = await FindBlockByCodeAsync(fromBlockCode, cancellationToken);
                    if (fromBlock != null)
                    {
                        var fromSlot = await _context.Set<YardSlot>()
                            .FirstOrDefaultAsync(s => s.YardBlockId == fromBlock.Id && s.Bay == fromBay && s.Row == fromRow && s.Tier == fromTier, cancellationToken);
                        if (fromSlot != null)
                        {
                            await _context.Database.ExecuteSqlInterpolatedAsync(
                                $"UPDATE yard_slots SET status = 'empty'::yard_slot_status WHERE id = {fromSlot.Id}", cancellationToken);
                        }
                    }
                }

                // Chiếm slot mới
                var targetLoc = task.CompletedLocation ?? task.ToLocation;
                if (!string.IsNullOrWhiteSpace(targetLoc))
                {
                    var (toBlockCode, toBay, toRow, toTier) = ParseSlotLocation(targetLoc);
                    var toBlock = await FindBlockByCodeAsync(toBlockCode, cancellationToken);
                    if (toBlock != null)
                    {
                        var toSlot = await _context.Set<YardSlot>()
                            .FirstOrDefaultAsync(s => s.YardBlockId == toBlock.Id && s.Bay == toBay && s.Row == toRow && s.Tier == toTier, cancellationToken);
                        if (toSlot != null)
                        {
                            await _context.Database.ExecuteSqlInterpolatedAsync(
                                $"UPDATE yard_slots SET status = 'occupied'::yard_slot_status WHERE id = {toSlot.Id}", cancellationToken);

                            var newPos = new ContainerPosition(container.Id, toSlot.Id);
                            await _context.Set<ContainerPosition>().AddAsync(newPos, cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (isRelocation) throw;
                _logger.LogWarning(ex, "Could not sync yard_slots/container_positions for task {TaskId}", task.Id);
            }
        }

        // Save Yard Operation History Event
        var opEvent = new YardOperationEvent
        {
            OperationId = task.Id,
            ContainerId = task.ContainerId ?? container?.Id ?? Guid.NewGuid(),
            DriverId = task.DriverId ?? Guid.Empty,
            OperationStatus = "Completed",
            EventType = "YardOperationCompleted",
            DeliveryStatus = "Published",
            OccurredAt = DateTime.UtcNow,
            PublishedAt = DateTime.UtcNow
        };
        await _context.Set<YardOperationEvent>().AddAsync(opEvent, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        await completionTransaction.CommitAsync(cancellationToken);

        int durationMinutes = task.StartTime.HasValue 
            ? (int)Math.Max(1, (task.EndTime.Value - task.StartTime.Value).TotalMinutes) 
            : 0;

        // Send Realtime Notification to Driver, Operator & Dispatcher (NXP-044 / NXP-119)
        try
        {
            var isRelocate = string.Equals(task.OperationType, "Relocate", StringComparison.OrdinalIgnoreCase);
            var title = isRelocate
                ? $"Hoàn thành chuyển container {task.ContainerNo} sang Block {task.BlockCode}"
                : $"Hoàn thành cẩu container {task.ContainerNo}";
            var message = isRelocate
                ? $"Lệnh {task.TaskCode}: Container {task.ContainerNo} đã được chuyển an toàn từ {task.FromLocation} đến {task.CompletedLocation} (Block {task.BlockCode}) lúc {task.EndTime:HH:mm dd/MM/yyyy}. Thời gian thực hiện: {durationMinutes} phút. Phí: {task.InternalFee?.ToString("N0") ?? "0"} VND. Thiết bị {task.EquipmentCode ?? "RTG"} đã được giải phóng."
                : $"Container {task.ContainerNo} đã được cẩu hạ bãi an toàn tại {task.CompletedLocation} (Block {task.BlockCode}) lúc {task.EndTime:HH:mm dd/MM/yyyy}. Thiết bị {task.EquipmentCode} đã được giải phóng.";

            await _notificationService.SendAsync(new SendNotificationDto
            {
                RecipientId = task.OperatorId ?? task.DriverId ?? Guid.Empty,
                Type = NotificationType.YardOperationCompleted,
                Severity = NotificationSeverity.Success,
                Title = title,
                Message = message,
                ReferenceId = task.TaskCode
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send complete notification for task {TaskId}", task.Id);
        }

        // Publish Realtime Dispatcher Event
        try
        {
            await _messageBroker.PublishAsync("dispatcher.status.updated", new DispatcherStatusUpdatedEvent(
                Guid.NewGuid(), "yard_task", task.Id.ToString(), "Completed", DateTime.UtcNow,
                Label: $"Hoàn thành cẩu container {task.ContainerNo} tại vị trí {task.CompletedLocation}"), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish realtime complete-lift event for {TaskId}", task.Id);
        }

        return MapToDto(task);
    }


    // Ảnh nhận container lưu trên S3 (không lưu ảnh trong database); lỗi tải ảnh làm hủy xác nhận để không mất bằng chứng.
    private async Task<List<string>> UploadReceiptPhotosAsync(List<string>? images, string containerNo, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        foreach (var image in (images ?? new List<string>()).Take(4))
        {
            var comma = image.IndexOf(',');
            if (!image.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || comma < 0)
                throw new ValidationException("PhotoImages", "Ảnh container không hợp lệ.");
            var contentType = image.Substring(5, image.IndexOf(';') - 5);
            var bytes = Convert.FromBase64String(image[(comma + 1)..]);
            if (bytes.Length > 6 * 1024 * 1024) throw new ValidationException("PhotoImages", "Mỗi ảnh tối đa 6 MB.");
            var extension = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            urls.Add(await _s3.UploadBytesAsync(bytes, $"{containerNo}{extension}", contentType, "NexusPort/yard-receipts", cancellationToken));
        }
        return urls;
    }

    public async Task<YardTaskDto> ReceiveContainerAtYardAsync(YardReceivingInspectionDto dto, string userId, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        var cleanContainerNo = dto.ContainerNo?.Trim().ToUpperInvariant() ?? string.Empty;
        var cleanActualSeal = dto.ActualSealNo?.Trim() ?? string.Empty;

        // 1. Tìm Task tương ứng
        YardTask? task = null;
        if (dto.TaskId != null && dto.TaskId != Guid.Empty)
        {
            task = await _context.Set<YardTask>().FindAsync(new object[] { dto.TaskId.Value }, cancellationToken);
        }
        if (task == null && !string.IsNullOrWhiteSpace(cleanContainerNo))
        {
            // Lấy nhiệm vụ mới nhất của container (xe hạ bãi vừa qua cổng), không lấy nhiệm vụ cũ.
            task = await _context.Set<YardTask>()
                .Where(t => t.ContainerNo.ToUpper() == cleanContainerNo && t.Status != "Cancelled")
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // 2. Tìm hoặc cập nhật Container
        var container = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == cleanContainerNo, cancellationToken);

        var snapshot = await YardContainerSnapshot.ReadAsync(_context,cleanContainerNo,cancellationToken);
        if (snapshot?.VisitId == null) throw new ValidationException("Container", "Container chưa có lượt đang lưu bãi để tiếp nhận.");
        string expectedSeal = snapshot.SealNumber ?? string.Empty;
        bool isSealIntact = dto.IsSealIntact && (string.IsNullOrEmpty(expectedSeal) || string.Equals(cleanActualSeal, expectedSeal, StringComparison.OrdinalIgnoreCase));

        // 3. Nếu chưa có Task, tạo Task mới ở trạng thái Assigned
        if (task == null)
        {
            task = new YardTask(
                $"TASK-Y-{new Random().Next(200, 999)}",
                cleanContainerNo,
                dto.YardBlockCode ?? "A01",
                "Unload",
                "GATE-IN",
                dto.LocationCoordinate ?? "A01-05-02-3",
                "High",
                "Assigned",
                snapshot.TypeCode,
                "Hàng Tiếp Nhận",
                null,
                null,
                DateTime.UtcNow.AddMinutes(30),
                dto.Notes
            );
            await _context.Set<YardTask>().AddAsync(task, cancellationToken);
        }

        // Cập nhật thông tin nhận bãi
        task.ReceivedAt = DateTime.UtcNow;
        task.ReceivedBy = string.IsNullOrWhiteSpace(userId) ? (dto.InspectorName ?? "Yard Staff") : userId;
        task.ActualSealNo = cleanActualSeal;
        task.ExpectedSealNo = expectedSeal;
        task.Condition = dto.Condition;
        if (!string.IsNullOrWhiteSpace(dto.LocationCoordinate))
        {
            task.ToLocation = dto.LocationCoordinate;
        }
        if (!string.IsNullOrWhiteSpace(dto.YardBlockCode))
        {
            task.BlockCode = dto.YardBlockCode;
        }

        // 4. Tạo bản ghi YardReceipt
        var receipt = new YardReceipt
        {
            ContainerId = container?.Id ?? task.ContainerId ?? Guid.NewGuid(),
            ContainerNo = cleanContainerNo,
            ExpectedSealNo = expectedSeal,
            ActualSealNo = cleanActualSeal,
            IsSealIntact = isSealIntact,
            IsMatchingContainer = true,
            Condition = dto.Condition,
            Notes = dto.Notes,
            ReceivedAt = DateTime.UtcNow,
            ReceivedBy = task.ReceivedBy,
            InspectorName = string.IsNullOrWhiteSpace(dto.InspectorName) ? "Nhân viên Bãi" : dto.InspectorName,
            YardBlockCode = task.BlockCode,
            LocationCoordinate = task.ToLocation ?? "A01-05-02-3"
        };
        receipt.PhotoUrls = System.Text.Json.JsonSerializer.Serialize(await UploadReceiptPhotosAsync(dto.PhotoImages, cleanContainerNo, cancellationToken));
        await _context.Set<YardReceipt>().AddAsync(receipt, cancellationToken);

        // 5. Cập nhật trạng thái Container -> In_Yard
        if (container != null)
        {
            container.Status = "in_yard";
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Phát Notification
        try
        {
            await _notificationService.SendAsync(new SendNotificationDto
            {
                RecipientId = Guid.Empty,
                Type = NotificationType.YardOperationCompleted,
                Severity = isSealIntact ? NotificationSeverity.Success : NotificationSeverity.Warning,
                Title = $"Đã tiếp nhận Container {cleanContainerNo} vào bãi",
                Message = $"Container {cleanContainerNo} đã đối soát và nhận vào Bãi {task.BlockCode} ô {task.ToLocation}. Trạng thái vỏ: {dto.Condition}. Chì seal: {(isSealIntact ? "Khớp chuẩn 🟢" : "Lệch/Rách 🔴")}.",
                ReferenceId = cleanContainerNo
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send receiving notification for {ContainerNo}", cleanContainerNo);
        }

        return MapToDto(task);
    }

    public async Task<IReadOnlyList<YardOperatorDto>> GetAvailableOperatorsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        var defaultOperators = new List<YardOperatorDto>
        {
            new() { Id = Guid.Parse("5cc0a2e6-0854-4109-8057-c3e64318a12d"), Username = "yard01", FullName = "Phạm Bãi Hàng (Cần thủ RTG 01)", Role = "Yard Operator", IsAvailable = true },
            new() { Id = Guid.Parse("7bb1a2e6-0854-4109-8057-c3e64318a12e"), Username = "yard02", FullName = "Nguyễn Văn Nam (Cần thủ RTG 02)", Role = "Yard Operator", IsAvailable = true },
            new() { Id = Guid.Parse("8cc2a2e6-0854-4109-8057-c3e64318a12f"), Username = "yard03", FullName = "Trần Đình Trọng (Cần thủ RS 01)", Role = "Yard Operator", IsAvailable = true },
            new() { Id = Guid.Parse("9dd3a2e6-0854-4109-8057-c3e64318a130"), Username = "yard04", FullName = "Lê Hoàng Đức (Cần thủ RTG 03)", Role = "Yard Operator", IsAvailable = false, CurrentTaskCode = "TASK-Y-999" }
        };

        // Lấy danh sách Task đang In_Progress để kiểm tra ai đang bận
        var inProgressTasks = await _context.Set<YardTask>()
            .Where(t => t.Status.ToLower() == "in_progress" && t.OperatorId != null)
            .ToListAsync(cancellationToken);

        var busyMap = inProgressTasks.ToDictionary(t => t.OperatorId!.Value, t => t.TaskCode);

        foreach (var op in defaultOperators)
        {
            if (busyMap.TryGetValue(op.Id, out var taskCode))
            {
                op.IsAvailable = false;
                op.CurrentTaskCode = taskCode;
            }
        }

        return defaultOperators;
    }

    public async Task<YardTaskDto> CreateTaskAsync(CreateYardTaskDto dto, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        var task = new YardTask(
            dto.TaskCode,
            dto.ContainerNo,
            dto.BlockCode,
            dto.OperationType,
            dto.FromLocation,
            dto.ToLocation,
            dto.Priority,
            "Assigned",
            dto.ContainerType,
            dto.CargoType,
            dto.VehiclePlate,
            dto.DriverName,
            dto.DueTime,
            dto.Notes
        );

        await _context.Set<YardTask>().AddAsync(task, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(task);
    }

    /// <summary>NXP-119: Tạo lệnh chuyển container giữa các Block</summary>
    public async Task<YardTaskDto> CreateRelocationTaskAsync(CreateRelocationTaskDto dto, string createdBy, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(dto.ContainerNo))
        {
            throw new ValidationException("ContainerNo", "Mã Container không được để trống.");
        }
        if (string.IsNullOrWhiteSpace(dto.ToLocation))
        {
            throw new ValidationException("ToLocation", "Vị trí đích không được để trống.");
        }

        var cleanContainerNo = dto.ContainerNo.Trim().ToUpperInvariant();

        // 1. Tìm container trong DB nếu có
        var container = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == cleanContainerNo, cancellationToken);

        // 2. Xác định vị trí gốc nếu chưa có
        string fromLocation = dto.FromLocation?.Trim() ?? string.Empty;
        string sourceBlock = dto.SourceBlockCode?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(fromLocation) && container != null)
        {
            var currentPosition = await _context.Set<ContainerPosition>()
                .Include(cp => cp.Slot)
                .ThenInclude(s => s.Block)
                .FirstOrDefaultAsync(cp => cp.ContainerId == container.Id && cp.IsCurrent, cancellationToken);

            if (currentPosition?.Slot != null)
            {
                var blockCode = currentPosition.Slot.Block?.Code ?? "A01";
                fromLocation = $"{blockCode}-{currentPosition.Slot.Bay:D2}-{currentPosition.Slot.Row:D2}-{currentPosition.Slot.Tier}";
                if (string.IsNullOrWhiteSpace(sourceBlock))
                {
                    sourceBlock = blockCode;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(fromLocation))
        {
            fromLocation = "A01-01-01-1";
            sourceBlock = "A01";
        }

        var physicalSnapshot = await YardContainerSnapshot.ReadAsync(_context,cleanContainerNo,cancellationToken);

        // 3. Validate Target Slot
        var validationResult = await ValidateTargetSlotAsync(new ValidateSlotRequestDto
        {
            TargetLocation = dto.ToLocation,
            ContainerNo = cleanContainerNo,
            ContainerType = physicalSnapshot?.TypeCode ?? string.Empty
        }, cancellationToken);

        if (!validationResult.IsValid)
        {
            throw new ValidationException("ToLocation", $"Vị trí đích [{dto.ToLocation}] không hợp lệ: {validationResult.Message}");
        }

        // 4. Tính chi phí di chuyển nội bộ
        decimal internalFee = dto.InternalFee ?? 0;
        if (dto.InternalFee == null)
        {
            var feeCalc = await CalculateShiftingFeeAsync(new CalculateShiftingFeeRequestDto
            {
                ContainerNo = cleanContainerNo,
                ContainerType = physicalSnapshot?.TypeCode ?? string.Empty,
                ShiftingReason = dto.ShiftingReason,
                IsBillable = dto.IsBillable
            }, cancellationToken);
            internalFee = feeCalc.Fee;
        }

        // 5. Sinh mã lệnh
        var randomSuffix = new Random().Next(1000, 9999);
        var taskCode = $"REL-{DateTime.UtcNow:yyyyMMdd}-{randomSuffix}";

        string targetBlock = !string.IsNullOrWhiteSpace(dto.TargetBlockCode) 
            ? dto.TargetBlockCode 
            : validationResult.BlockCode;
        if (string.IsNullOrWhiteSpace(targetBlock)) targetBlock = "B02";

        var task = new YardTask(
            taskCode: taskCode,
            containerNo: cleanContainerNo,
            blockCode: targetBlock,
            operationType: "Relocate",
            fromLocation: fromLocation,
            toLocation: dto.ToLocation.Trim(),
            priority: string.IsNullOrWhiteSpace(dto.Priority) ? "Normal" : dto.Priority,
            status: dto.EquipmentId.HasValue ? "Ready" : "Assigned",
            containerType: dto.ContainerType ?? "40FT HC",
            cargoType: container?.Description ?? "Hàng Khô",
            dueTime: DateTime.UtcNow.AddHours(2),
            notes: dto.Notes,
            internalFee: internalFee,
            shiftingReason: dto.ShiftingReason ?? "Tái cơ cấu xếp bãi"
        );

        task.ContainerId = container?.Id;
        task.AssignedBy = string.IsNullOrWhiteSpace(createdBy) ? "Operator - Điều Phối Bãi" : createdBy;
        task.AssignedAt = DateTime.UtcNow;

        // Gán thiết bị nếu có
        if (dto.EquipmentId.HasValue)
        {
            var eq = await _context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>()
                .FindAsync(new object[] { dto.EquipmentId.Value }, cancellationToken);
            if (eq != null)
            {
                task.EquipmentId = eq.Id;
                task.EquipmentCode = eq.EquipmentCode;
                task.EquipmentType = eq.EquipmentType;
            }
        }
        if (dto.OperatorId.HasValue)
        {
            task.OperatorId = dto.OperatorId.Value;
            task.OperatorName = dto.OperatorName ?? "Cần Thủ Ca Trực";
        }

        await _context.Set<YardTask>().AddAsync(task, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        // Gửi thông báo cho Operator & Dispatcher
        try
        {
            await _notificationService.SendAsync(new SendNotificationDto
            {
                RecipientId = dto.OperatorId ?? Guid.Empty,
                Type = NotificationType.YardOperationCompleted,
                Severity = NotificationSeverity.Info,
                Title = $"Lệnh chuyển container mới: {task.TaskCode}",
                Message = $"Lệnh chuyển container {task.ContainerNo} từ {task.FromLocation} sang {task.ToLocation} đã được tạo. Ưu tiên: {task.Priority}. Lý do: {task.ShiftingReason}.",
                ReferenceId = task.TaskCode
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send relocation task creation notification for {TaskCode}", task.TaskCode);
        }

        // Publish broker event
        try
        {
            await _messageBroker.PublishAsync("dispatcher.status.updated", new DispatcherStatusUpdatedEvent(
                Guid.NewGuid(), "yard_task", task.Id.ToString(), task.Status, DateTime.UtcNow,
                Label: $"Tạo lệnh chuyển container {task.ContainerNo}: {task.FromLocation} ➔ {task.ToLocation}"), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish realtime relocation task creation event for {TaskCode}", task.TaskCode);
        }

        return MapToDto(task);
    }

    /// <summary>NXP-119: Kiểm tra Slot đích (Trọng lực, Sức chứa, Trạng thái)</summary>
    public async Task<ValidateSlotResponseDto> ValidateTargetSlotAsync(ValidateSlotRequestDto dto, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        var response = new ValidateSlotResponseDto
        {
            TargetLocation = dto.TargetLocation,
            IsValid = true,
            Message = "Vị trí đích hợp lệ và sẵn sàng tiếp nhận container.",
            Warnings = new List<string>()
        };

        if (string.IsNullOrWhiteSpace(dto.TargetLocation))
        {
            response.IsValid = false;
            response.Message = "Vị trí đích không được để trống.";
            return response;
        }

        var (blockCode, bay, row, tier) = ParseSlotLocation(dto.TargetLocation);
        response.BlockCode = blockCode;
        response.Bay = bay;
        response.Row = row;
        response.Tier = tier;

        if (bay <= 0 || row <= 0 || tier <= 0)
        {
            response.IsValid = false;
            response.Message = "Định dạng tọa độ bãi không hợp lệ (Mẫu: Block-Bay-Row-Tier, ví dụ: B02-04-01-2).";
            return response;
        }

        // Tìm Block
        var block = await FindBlockByCodeAsync(blockCode, cancellationToken);
        if (block == null)
        {
            block = await _context.Set<YardBlock>().FirstOrDefaultAsync(b => b.Code.StartsWith(blockCode) || blockCode.StartsWith(b.Code), cancellationToken);
        }

        if (block != null)
        {
            response.BlockCode = block.Code;

            // Quy tắc phân khu: container lạnh chỉ vào khu lạnh; container thường không được vào khu lạnh
            var typeText = (dto.ContainerType ?? string.Empty).ToUpperInvariant();
            var isReeferContainer = typeText.Contains("RF") || typeText.Contains("RH") || typeText.Contains("REEFER");
            if (!isReeferContainer && !string.IsNullOrWhiteSpace(dto.ContainerNo))
            {
                var flag = await _context.Database.SqlQueryRaw<bool>(
                    "SELECT COALESCE(bool_or(is_reefer), false) AS \"Value\" FROM containers WHERE upper(container_no)={0}",
                    dto.ContainerNo.Trim().ToUpperInvariant()).FirstOrDefaultAsync(cancellationToken);
                isReeferContainer = flag;
            }
            var isReeferZone = block.IsReeferArea || block.Code.StartsWith("D", StringComparison.OrdinalIgnoreCase) || string.Equals(block.Code, "R", StringComparison.OrdinalIgnoreCase);
            if (isReeferContainer && !isReeferZone)
            {
                response.IsValid = false;
                response.Message = $"Container lạnh chỉ được xếp vào khu lạnh (có ổ cắm điện), không thể xếp vào Block {block.Code}.";
                return response;
            }
            if (!isReeferContainer && isReeferZone)
            {
                response.IsValid = false;
                response.Message = $"Container thường không được xếp vào khu lạnh (Block {block.Code}). Hãy chọn khu bãi thường.";
                return response;
            }

            // Tìm Slot trong DB
            var slot = await _context.Set<YardSlot>()
                .FirstOrDefaultAsync(s => s.YardBlockId == block.Id && s.Bay == bay && s.Row == row && s.Tier == tier, cancellationToken);

            if (slot != null)
            {
                response.SlotStatus = slot.Status;
                if (slot.Status.ToLower() == "occupied")
                {
                    response.IsValid = false;
                    response.Message = $"Slot [{dto.TargetLocation}] đã có container chiếm chỗ!";
                    return response;
                }
                if (slot.Status.ToLower() == "maintenance")
                {
                    response.IsValid = false;
                    response.Message = $"Slot [{dto.TargetLocation}] đang bảo trì kỹ thuật!";
                    return response;
                }
                if (slot.Status.ToLower() == "reserved")
                {
                    response.IsValid = false;
                    response.Message = $"Slot [{dto.TargetLocation}] đã được đặt chỗ trước!";
                    return response;
                }
            }

            // Kiểm tra Quy tắc Trọng Lực (Gravity Rule): Nếu Tier > 1, kiểm tra Tier - 1
            if (tier > 1)
            {
                var lowerSlot = await _context.Set<YardSlot>()
                    .FirstOrDefaultAsync(s => s.YardBlockId == block.Id && s.Bay == bay && s.Row == row && s.Tier == tier - 1, cancellationToken);

                if (lowerSlot != null && lowerSlot.Status.ToLower() != "occupied" && lowerSlot.Status.ToLower() != "reserved")
                {
                    response.IsValid = false;
                    response.GravitySatisfied = false;
                    response.Message = $"❌ Vi phạm an toàn xếp tầng: Tầng dưới (Tier {tier - 1}) hiện đang trống ({lowerSlot.Status}). Container không thể lơ lửng trên không, bắt buộc phải xếp từ nền bãi (Tier 1) lên.";
                    return response;
                }
            }

            // Kiểm tra Quy tắc Kích thước & Góc chịu lực (Corner Castings):
            var is40Ft = dto.ContainerType != null && (dto.ContainerType.Contains("40") || dto.ContainerType.Contains("45"));
            if (tier > 1 && is40Ft)
            {
                var bay1 = bay % 2 == 0 ? bay - 1 : bay;
                var bay2 = bay % 2 == 0 ? bay + 1 : bay + 2;

                var lower1 = await _context.Set<YardSlot>()
                    .FirstOrDefaultAsync(s => s.YardBlockId == block.Id && s.Bay == bay1 && s.Row == row && s.Tier == tier - 1, cancellationToken);
                var lower2 = await _context.Set<YardSlot>()
                    .FirstOrDefaultAsync(s => s.YardBlockId == block.Id && s.Bay == bay2 && s.Row == row && s.Tier == tier - 1, cancellationToken);

                var occ1 = lower1 != null && (lower1.Status.ToLower() == "occupied" || lower1.Status.ToLower() == "reserved");
                var occ2 = lower2 != null && (lower2.Status.ToLower() == "occupied" || lower2.Status.ToLower() == "reserved");

                if (!occ1 && !occ2)
                {
                    response.IsValid = false;
                    response.Message = $"❌ Vi phạm quy tắc đỡ tầng: Tầng dưới (Tier {tier - 1}) tại Bay {bay1} và Bay {bay2} đều đang trống. Container không thể lơ lửng.";
                    return response;
                }

                if ((occ1 && !occ2) || (!occ1 && occ2))
                {
                    response.IsValid = false;
                    response.Message = "❌ Vi phạm góc chịu lực kết cấu: Không thể đặt container 40ft lên trên chỉ 1 container 20ft đơn lẻ (4 trụ góc sẽ bị hẫng lơ lửng gây sập đổ). Tầng dưới bắt buộc phải là container 40ft hoặc đủ 2 container 20ft đỡ 4 góc!";
                    return response;
                }
            }

            var is20Ft = dto.ContainerType != null && dto.ContainerType.Contains("20");
            if (tier > 1 && is20Ft)
            {
                var lowerSlot = await _context.Set<YardSlot>()
                    .FirstOrDefaultAsync(s => s.YardBlockId == block.Id && s.Bay == bay && s.Row == row && s.Tier == tier - 1, cancellationToken);

                if (lowerSlot != null && lowerSlot.ContainerLengthFt == 40)
                {
                    response.IsValid = false;
                    response.Message = "❌ Vi phạm kỹ thuật chất xếp bãi: Không được xếp container 20ft lên trên container 40ft, vì gù chịu lực giữa của cont 20ft sẽ ấn vào giữa nóc cont 40ft gây bẹp thủng nóc!";
                    return response;
                }
            }
        }
        else
        {
            // Block chưa khai báo trong DB nhưng cho phép nếu tọa độ hợp lệ
            if (tier > 5)
            {
                response.IsValid = false;
                response.Message = $"Tầng cẩu (Tier {tier}) vượt quá chiều cao an toàn tối đa cho phép (Tối đa 5 tầng).";
                return response;
            }
        }

        return response;
    }

    /// <summary>NXP-119: Tính chi phí di chuyển nội bộ nếu có</summary>
    public Task<CalculateShiftingFeeResponseDto> CalculateShiftingFeeAsync(CalculateShiftingFeeRequestDto dto, CancellationToken cancellationToken = default)
    {
        // Nghiệp vụ tính phí chuyển bãi:
        // - Yêu cầu từ Hãng tàu/Khách hàng (Billable): 20FT = 350,000 VND, 40FT/45FT = 550,000 VND
        // - Nội bộ cảng (Housekeeping, tái cơ cấu bãi, chuẩn bị cẩu dỡ): 0 VND (Miễn phí tác nghiệp nội bộ)
        decimal fee = 0;
        string desc = "Tác nghiệp đảo chuyển nội bộ cảng (Miễn phí)";

        if (dto.IsBillable || string.Equals(dto.ShiftingReason, "Theo yêu cầu khách hàng", StringComparison.OrdinalIgnoreCase))
        {
            var type = dto.ContainerType?.ToUpperInvariant() ?? "40FT";
            if (type.Contains("20"))
            {
                fee = 350000;
                desc = "Phí đảo chuyển container 20FT theo yêu cầu khách hàng (350.000 VND)";
            }
            else
            {
                fee = 550000;
                desc = "Phí đảo chuyển container 40FT/45FT theo yêu cầu khách hàng (550.000 VND)";
            }
        }

        return Task.FromResult(new CalculateShiftingFeeResponseDto
        {
            Fee = fee,
            Currency = "VND",
            Description = desc
        });
    }

    /// <summary>NXP-119: Lấy danh sách lệnh di chuyển container giữa các Block</summary>
    public async Task<IReadOnlyList<YardTaskDto>> GetRelocationTasksAsync(string? status, CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        var query = _context.Set<YardTask>()
            .Where(t => t.OperationType == "Relocate" || t.OperationType.Contains("chuyển") || t.OperationType.Contains("Chuyển") || t.OperationType.Contains("Đảo") || t.OperationType.Contains("Reloc") ||
                // Nhiệm vụ hạ bãi tự tạo khi xe qua cổng vào cũng được điều phối ở trang Lệnh di chuyển container.
                (t.OperationType == "Unload" && t.CreatedBy == "system:gate-in"));

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(t => t.Status.ToLower() == status.ToLower());
        }

        var tasks = await query.OrderByDescending(t => t.CreatedAt).ToListAsync(cancellationToken);
        return tasks.Select(MapToDto).ToList();
    }

    public async Task EnsureSeedTasksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);
    }

    /// <summary>NXP-119: Lấy danh sách container thực tế đang nằm trong bãi từ database</summary>
    public async Task<IReadOnlyList<ActiveYardContainerDto>> GetActiveYardContainersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);

        var result = new List<ActiveYardContainerDto>();
        var seenContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Quét vị trí thực tế hiện tại từ ContainerPosition (IsCurrent = true)
        try
        {
            var positions = await _context.Set<ContainerPosition>()
                .Include(p => p.Slot).ThenInclude(s => s.Block)
                .Where(p => p.IsCurrent && p.Slot != null && p.Slot.Block != null)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            if (positions.Count > 0)
            {
                var containerIds = positions.Select(p => p.ContainerId).Distinct().ToList();
                var containers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
                    .Where(c => containerIds.Contains(c.Id))
                    .AsNoTracking()
                    .ToDictionaryAsync(c => c.Id, cancellationToken);

                foreach (var pos in positions)
                {
                    if (pos.Slot?.Block == null) continue;
                    if (!containers.TryGetValue(pos.ContainerId, out var container)) continue;
                    var cNo = container.ContainerNumber?.Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(cNo) || !seenContainers.Add(cNo)) continue;

                    var loc = LiveRestackAnalyzer.Location(pos.Slot);
                    var rawType = pos.Slot.ContainerLengthFt.HasValue ? $"{pos.Slot.ContainerLengthFt}FT" : "40FT HC";
                    var normType = NormalizeContainerType(rawType, container.CargoType);
                    var line = DetectShippingLine(cNo);
                    var isReefer = normType.Contains("RF") || normType.Contains("RH") || pos.Slot.HasReeferPlug;

                    result.Add(new ActiveYardContainerDto
                    {
                        ContainerNo = cNo,
                        Location = loc,
                        Type = normType,
                        Line = line,
                        Cargo = string.IsNullOrWhiteSpace(container.CargoType) ? (isReefer ? "Hàng Lạnh (Reefer)" : "Hàng Khô Đang Lưu Bãi") : container.CargoType,
                        Weight = "28.5 Tấn",
                        Category = isReefer ? "REEFER" : (normType.Contains("TANK") ? "TANK" : "DRY"),
                        DwellDays = 2,
                        NextVessel = $"{line} EXPRESS"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi khi truy vấn ContainerPositions");
        }

        // 2. Bổ sung từ các tác nghiệp bãi thực tế (YardTask) đã tiếp nhận hoặc đã hoàn thành
        if (result.Count < 12)
        {
            try
            {
                var tasks = await _context.Set<YardTask>()
                    .Where(t => !string.IsNullOrEmpty(t.ContainerNo) &&
                               (!string.IsNullOrEmpty(t.CompletedLocation) || !string.IsNullOrEmpty(t.ToLocation) || !string.IsNullOrEmpty(t.FromLocation)))
                    .OrderByDescending(t => t.CreatedAt)
                    .Take(50)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);

                foreach (var t in tasks)
                {
                    var cNo = t.ContainerNo?.Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(cNo) || !seenContainers.Add(cNo)) continue;

                    var rawLoc = !string.IsNullOrEmpty(t.CompletedLocation)
                        ? t.CompletedLocation
                        : (!string.IsNullOrEmpty(t.ToLocation) ? t.ToLocation : t.FromLocation);

                    var (bCode, bBay, bRow, bTier) = ParseSlotLocation(rawLoc ?? "");
                    if (!string.IsNullOrEmpty(bCode) && bCode.Length == 1)
                    {
                        bCode = $"{bCode}01";
                    }
                    var formattedLoc = !string.IsNullOrEmpty(bCode) && bBay > 0 && bRow > 0 && bTier > 0
                        ? $"{bCode}-{bBay:D2}-{bRow:D2}-{bTier}"
                        : (rawLoc ?? "A01-01-01-1");

                    // Nếu địa chỉ còn chứa ký tự không chuẩn (ví dụ "Cầu Tàu"), chuyển về block bãi hợp lệ
                    if (formattedLoc.Contains("Cầu") || formattedLoc.Contains("Tàu") || formattedLoc.Length < 7)
                    {
                        var block = !string.IsNullOrEmpty(t.BlockCode) && !t.BlockCode.Contains("Cầu") ? t.BlockCode : "A01";
                        formattedLoc = $"{block}-01-01-1";
                    }

                    var normType = NormalizeContainerType(t.ContainerType, t.CargoType);
                    var line = DetectShippingLine(cNo);
                    var isReefer = normType.Contains("RF") || normType.Contains("RH") || (t.CargoType?.ToLower().Contains("lạnh") == true);

                    result.Add(new ActiveYardContainerDto
                    {
                        ContainerNo = cNo,
                        Location = formattedLoc,
                        Type = normType,
                        Line = line,
                        Cargo = string.IsNullOrWhiteSpace(t.CargoType) ? (isReefer ? "Hàng Lạnh (Reefer)" : "Hàng Khô Đang Lưu Bãi") : t.CargoType,
                        Weight = "29.2 Tấn",
                        Category = isReefer ? "REEFER" : (normType.Contains("TANK") ? "TANK" : "DRY"),
                        DwellDays = Math.Max(1, (int)(DateTime.UtcNow - t.CreatedAt).TotalDays),
                        NextVessel = $"{line} FEEDER"
                    });

                    if (result.Count >= 12) break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi bổ sung container từ YardTasks");
            }
        }

        return result;
    }

    private static string DetectShippingLine(string containerNo)
    {
        if (string.IsNullOrWhiteSpace(containerNo)) return "COSCO";
        var prefix = containerNo.Trim().ToUpperInvariant();
        if (prefix.StartsWith("MSC")) return "MSC";
        if (prefix.StartsWith("TEMU") || prefix.StartsWith("MAEU") || prefix.StartsWith("MSK")) return "MAERSK";
        if (prefix.StartsWith("CMA")) return "CMA CGM";
        if (prefix.StartsWith("COS")) return "COSCO";
        if (prefix.StartsWith("HAM") || prefix.StartsWith("HLX") || prefix.StartsWith("HLC")) return "HAPAG-LLOYD";
        if (prefix.StartsWith("ONE")) return "ONE";
        if (prefix.StartsWith("EMC") || prefix.StartsWith("EVE")) return "EVERGREEN";
        if (prefix.StartsWith("NXP")) return "NEXUSPORT";
        return "MAERSK";
    }

    private static string NormalizeContainerType(string? rawType, string? cargoType)
    {
        var type = rawType?.ToUpperInvariant() ?? "";
        var isReefer = (cargoType?.ToLower().Contains("lạnh") == true) || (cargoType?.ToLower().Contains("reefer") == true) || type.Contains("RF") || type.Contains("RH");
        var isTank = type.Contains("TK") || type.Contains("TANK");

        if (type.Contains("20"))
        {
            if (isReefer) return "20FT RF";
            if (isTank) return "20FT TANK";
            return "20FT ST";
        }

        if (isReefer) return "40FT RF";
        if (isTank) return "40FT TANK";
        return "40FT HC";
    }

    private static (string BlockCode, int Bay, int Row, int Tier) ParseSlotLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return (string.Empty, 0, 0, 0);

        // Formats supported: "A01-03-02-1", "A-03-12-2", "B02-04-01-2", "A01-3-2-1"
        var parts = location.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
        {
            string block = parts[0].Trim().ToUpperInvariant();
            int.TryParse(parts[1], out int bay);
            int.TryParse(parts[2], out int row);
            int.TryParse(parts[3], out int tier);
            return (block, bay, row, tier);
        }
        if (parts.Length == 3)
        {
            string block = parts[0].Trim().ToUpperInvariant();
            int.TryParse(parts[1], out int bay);
            int.TryParse(parts[2], out int row);
            return (block, bay, row, 1);
        }
        return (location.Trim().ToUpperInvariant(), 1, 1, 1);
    }

    private async Task<YardBlock?> FindBlockByCodeAsync(string blockCode, CancellationToken cancellationToken)
    {
        var cleanCode = blockCode.Trim().ToUpperInvariant();
        return await _context.Set<YardBlock>()
            .FirstOrDefaultAsync(b => b.Code.ToUpper() == cleanCode || b.Name.ToUpper().Contains(cleanCode), cancellationToken);
    }

    private static YardTaskDto MapToDto(YardTask t)
    {
        int? duration = null;
        if (t.StartTime.HasValue)
        {
            var end = t.EndTime ?? DateTime.UtcNow;
            duration = (int)Math.Max(0, (end - t.StartTime.Value).TotalMinutes);
        }

        string sourceBlock = t.BlockCode;
        string targetBlock = t.BlockCode;
        if (!string.IsNullOrWhiteSpace(t.FromLocation) && t.FromLocation.Contains("-"))
        {
            sourceBlock = t.FromLocation.Split('-')[0];
        }
        if (!string.IsNullOrWhiteSpace(t.ToLocation) && t.ToLocation.Contains("-"))
        {
            targetBlock = t.ToLocation.Split('-')[0];
        }

        return new YardTaskDto
        {
            Id = t.Id,
            TaskCode = t.TaskCode,
            OperationType = t.OperationType,
            ContainerId = t.ContainerId,
            ContainerNo = t.ContainerNo,
            ContainerType = t.ContainerType,
            CargoType = t.CargoType,
            VehicleId = t.VehicleId,
            VehiclePlate = t.VehiclePlate,
            DriverId = t.DriverId,
            DriverName = t.DriverName,
            FromLocation = t.FromLocation,
            ToLocation = t.ToLocation,
            BlockCode = t.BlockCode,
            EquipmentId = t.EquipmentId,
            EquipmentCode = t.EquipmentCode,
            EquipmentType = t.EquipmentType,
            OperatorId = t.OperatorId,
            OperatorName = t.OperatorName,
            Priority = t.Priority,
            Status = t.Status,
            DueTime = t.DueTime,
            AssignedAt = t.AssignedAt,
            AssignedBy = t.AssignedBy,
            StartTime = t.StartTime,
            EndTime = t.EndTime,
            ExpectedSealNo = t.ExpectedSealNo,
            ActualSealNo = t.ActualSealNo,
            Condition = t.Condition,
            ReceivedAt = t.ReceivedAt,
            ReceivedBy = t.ReceivedBy,
            CompletedLocation = t.CompletedLocation,
            InternalFee = t.InternalFee,
            ShiftingReason = t.ShiftingReason,
            DurationMinutes = duration,
            SourceBlockCode = sourceBlock,
            TargetBlockCode = targetBlock,
            Notes = t.Notes,
            CreatedAt = t.CreatedAt
        };
    }

    private async Task EnsureTableAndSeedAsync(CancellationToken cancellationToken)
    {
        if (_tableEnsured) return;

        try
        {
            var createTableSql = @"
CREATE TABLE IF NOT EXISTS ""Equipments"" (
    ""Id"" uuid PRIMARY KEY,
    ""EquipmentCode"" varchar(100) NOT NULL,
    ""Name"" varchar(150),
    ""EquipmentType"" varchar(50),
    ""BlockCode"" varchar(50),
    ""Status"" varchar(50) NOT NULL DEFAULT 'available',
    ""OperatorId"" uuid,
    ""OperatorName"" varchar(150),
    ""Description"" varchar(500),
    ""CreatedAt"" timestamptz NOT NULL DEFAULT now(),
    ""CreatedBy"" varchar(150),
    ""UpdatedAt"" timestamptz,
    ""UpdatedBy"" varchar(150),
    ""IsDeleted"" boolean NOT NULL DEFAULT false
);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""Name"" varchar(150);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""EquipmentType"" varchar(50);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""BlockCode"" varchar(50);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""Status"" varchar(50) DEFAULT 'available';
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""OperatorId"" uuid;
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""OperatorName"" varchar(150);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""Description"" varchar(500);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""CreatedAt"" timestamptz DEFAULT now();
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""CreatedBy"" varchar(150);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""UpdatedAt"" timestamptz;
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""UpdatedBy"" varchar(150);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""IsDeleted"" boolean NOT NULL DEFAULT false;
CREATE INDEX IF NOT EXISTS ix_equipments_code ON ""Equipments"" (""EquipmentCode"");
CREATE INDEX IF NOT EXISTS ix_equipments_status ON ""Equipments"" (""Status"");



CREATE TABLE IF NOT EXISTS yard_receipts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    container_id uuid,
    container_no varchar(50) NOT NULL,
    expected_seal_no varchar(50),
    actual_seal_no varchar(50),
    is_seal_intact boolean DEFAULT true,
    is_matching_container boolean DEFAULT true,
    condition varchar(50) DEFAULT 'Good',
    notes varchar(500),
    received_at timestamptz DEFAULT now(),
    received_by varchar(150),
    inspector_name varchar(150),
    yard_block_code varchar(50),
    location_coordinate varchar(50),
    created_at timestamptz DEFAULT now(),
    created_by varchar(150),
    updated_at timestamptz,
    updated_by varchar(150),
    is_deleted boolean DEFAULT false
);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS id uuid DEFAULT gen_random_uuid();
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS container_id uuid;
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS container_no varchar(50);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS expected_seal_no varchar(50);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS actual_seal_no varchar(50);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS is_seal_intact boolean DEFAULT true;
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS is_matching_container boolean DEFAULT true;
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS condition varchar(50);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS notes varchar(500);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS received_at timestamptz DEFAULT now();
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS received_by varchar(150);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS inspector_name varchar(150);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS yard_block_code varchar(50);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS location_coordinate varchar(50);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS created_at timestamptz DEFAULT now();
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS created_by varchar(150);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS updated_at timestamptz;
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS updated_by varchar(150);
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS is_deleted boolean DEFAULT false;
CREATE INDEX IF NOT EXISTS ix_yard_receipts_containerno ON yard_receipts (container_no);
CREATE INDEX IF NOT EXISTS ix_yard_receipts_receivedat ON yard_receipts (received_at);

CREATE TABLE IF NOT EXISTS yard_operation_events (
    ""Id"" uuid PRIMARY KEY,
    operation_id uuid NOT NULL,
    container_id uuid NOT NULL,
    driver_id uuid NOT NULL,
    operation_status varchar(50) NOT NULL,
    event_type varchar(100) NOT NULL,
    delivery_status varchar(20) NOT NULL,
    occurred_at timestamptz NOT NULL,
    published_at timestamptz,
    delivery_error varchar(1000),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_yard_op_events_driver_occurred ON yard_operation_events (driver_id, occurred_at);

CREATE TABLE IF NOT EXISTS yard_tasks (
    ""Id"" uuid PRIMARY KEY,
    ""TaskCode"" varchar(50) NOT NULL,
    ""OperationType"" varchar(50) NOT NULL,
    ""ContainerId"" uuid,
    ""ContainerNo"" varchar(50) NOT NULL,
    ""ContainerType"" varchar(50),
    ""CargoType"" varchar(150),
    ""VehicleId"" uuid,
    ""VehiclePlate"" varchar(50),
    ""DriverId"" uuid,
    ""DriverName"" varchar(150),
    ""FromLocation"" varchar(100),
    ""ToLocation"" varchar(100),
    ""BlockCode"" varchar(50) NOT NULL,
    ""EquipmentId"" uuid,
    ""EquipmentCode"" varchar(50),
    ""EquipmentType"" varchar(50),
    ""OperatorId"" uuid,
    ""OperatorName"" varchar(150),
    ""Priority"" varchar(30) NOT NULL DEFAULT 'Normal',
    ""Status"" varchar(50) NOT NULL DEFAULT 'Assigned',
    ""DueTime"" timestamptz,
    ""AssignedAt"" timestamptz,
    ""AssignedBy"" varchar(150),
    ""StartTime"" timestamptz,
    ""EndTime"" timestamptz,
    ""ExpectedSealNo"" varchar(50),
    ""ActualSealNo"" varchar(50),
    ""Condition"" varchar(50),
    ""ReceivedAt"" timestamptz,
    ""ReceivedBy"" varchar(150),
    ""CompletedLocation"" varchar(100),
    ""Notes"" varchar(500),
    ""CreatedAt"" timestamptz NOT NULL DEFAULT now(),
    ""CreatedBy"" varchar(150),
    ""UpdatedAt"" timestamptz,
    ""UpdatedBy"" varchar(150),
    ""IsDeleted"" boolean NOT NULL DEFAULT false
);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""TaskCode"" varchar(50);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""OperationType"" varchar(50) NOT NULL DEFAULT 'Hạ Bãi Nhập';
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""ContainerId"" uuid;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""ContainerNo"" varchar(50);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""ContainerType"" varchar(50);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""CargoType"" varchar(150);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""VehicleId"" uuid;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""VehiclePlate"" varchar(50);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""DriverId"" uuid;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""DriverName"" varchar(150);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""FromLocation"" varchar(100);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""ToLocation"" varchar(100);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""BlockCode"" varchar(50) NOT NULL DEFAULT 'A01';
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""EquipmentId"" uuid;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""EquipmentCode"" varchar(50);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""EquipmentType"" varchar(50);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""OperatorId"" uuid;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""OperatorName"" varchar(150);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""Priority"" varchar(30) NOT NULL DEFAULT 'Normal';
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""Status"" varchar(50) NOT NULL DEFAULT 'Assigned';
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""DueTime"" timestamptz;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""AssignedAt"" timestamptz;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""AssignedBy"" varchar(150);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""StartTime"" timestamptz;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""EndTime"" timestamptz;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""ExpectedSealNo"" varchar(50);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""ActualSealNo"" varchar(50);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""Condition"" varchar(50);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""ReceivedAt"" timestamptz;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""ReceivedBy"" varchar(150);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""CompletedLocation"" varchar(100);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""InternalFee"" numeric(12,2) DEFAULT 0;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""ShiftingReason"" varchar(250);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""Notes"" varchar(500);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""CreatedAt"" timestamptz NOT NULL DEFAULT now();
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""CreatedBy"" varchar(150);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""UpdatedAt"" timestamptz;
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""UpdatedBy"" varchar(150);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS ""IsDeleted"" boolean NOT NULL DEFAULT false;
CREATE INDEX IF NOT EXISTS ix_yard_tasks_taskcode ON yard_tasks (""TaskCode"");
CREATE INDEX IF NOT EXISTS ix_yard_tasks_blockcode ON yard_tasks (""BlockCode"");
CREATE INDEX IF NOT EXISTS ix_yard_tasks_status ON yard_tasks (""Status"");
";
            await _context.Database.ExecuteSqlRawAsync(createTableSql, cancellationToken);

            // Production data is entered through real workflows; never seed on reads.
            _tableEnsured = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring yard tables and seed data");
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private static bool _tableEnsured = false;

    public YardTaskService(
        AppDbContext context,
        IMessageBrokerService messageBroker,
        INotificationService notificationService,
        ILogger<YardTaskService> logger)
    {
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
            container.Status = "Handling";
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

        if (task.Status.ToLower() != "in_progress")
        {
            throw new ValidationException("Status", $"Nhiệm vụ {task.TaskCode} không ở trạng thái Đang cẩu (Hiện tại: {task.Status})!");
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

        // Send Realtime Notification to Driver & Dispatcher (NXP-044)
        try
        {
            await _notificationService.SendAsync(new SendNotificationDto
            {
                RecipientId = task.DriverId ?? Guid.Empty,
                Type = NotificationType.YardOperationCompleted,
                Severity = NotificationSeverity.Success,
                Title = $"Hoàn thành cẩu container {task.ContainerNo}",
                Message = $"Container {task.ContainerNo} đã được cẩu hạ bãi an toàn tại {task.CompletedLocation} (Block {task.BlockCode}) lúc {task.EndTime:HH:mm dd/MM/yyyy}. Thiết bị {task.EquipmentCode} đã được giải phóng.",
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
            task = await _context.Set<YardTask>()
                .FirstOrDefaultAsync(t => t.ContainerNo.ToUpper() == cleanContainerNo, cancellationToken);
        }

        // 2. Tìm hoặc cập nhật Container
        var container = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .FirstOrDefaultAsync(c => c.ContainerNumber.ToUpper() == cleanContainerNo, cancellationToken);

        string expectedSeal = dto.ExpectedSealNo ?? container?.SealNumber ?? "SEAL-889922";
        bool isSealIntact = dto.IsSealIntact && string.Equals(cleanActualSeal, expectedSeal, StringComparison.OrdinalIgnoreCase);

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
                container?.CargoType ?? "40FT HC",
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
        await _context.Set<YardReceipt>().AddAsync(receipt, cancellationToken);

        // 5. Cập nhật trạng thái Container -> In_Yard
        if (container != null)
        {
            container.Status = "In_Yard";
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

    public async Task EnsureSeedTasksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTableAndSeedAsync(cancellationToken);
    }

    private static YardTaskDto MapToDto(YardTask t)
    {
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

CREATE TABLE IF NOT EXISTS ""Containers"" (
    ""Id"" uuid PRIMARY KEY,
    ""CarrierId"" uuid,
    ""ContainerNumber"" varchar(100) NOT NULL,
    ""SealNumber"" varchar(100),
    ""Status"" varchar(50) NOT NULL DEFAULT 'in_yard',
    ""CargoType"" varchar(100),
    ""Description"" varchar(500),
    ""CreatedAt"" timestamptz NOT NULL DEFAULT now(),
    ""CreatedBy"" varchar(150),
    ""UpdatedAt"" timestamptz,
    ""UpdatedBy"" varchar(150),
    ""IsDeleted"" boolean NOT NULL DEFAULT false
);
ALTER TABLE ""Containers"" ADD COLUMN IF NOT EXISTS ""CreatedBy"" varchar(150);
ALTER TABLE ""Containers"" ADD COLUMN IF NOT EXISTS ""UpdatedBy"" varchar(150);
ALTER TABLE ""Containers"" ADD COLUMN IF NOT EXISTS ""IsDeleted"" boolean NOT NULL DEFAULT false;
ALTER TABLE ""Containers"" ADD COLUMN IF NOT EXISTS ""SealNumber"" varchar(100);
ALTER TABLE ""Containers"" ADD COLUMN IF NOT EXISTS ""CargoType"" varchar(100);
CREATE INDEX IF NOT EXISTS ix_containers_number ON ""Containers"" (""ContainerNumber"");
CREATE INDEX IF NOT EXISTS ix_containers_status ON ""Containers"" (""Status"");

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

            // 1. Seed Equipments nếu chưa có
            var eqCount = await _context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>().CountAsync(cancellationToken);
            if (eqCount == 0)
            {
                var equipments = new List<NexusPort.Modules.Equipment.Domain.Entities.Equipment>
                {
                    new("RTG-01", "Cẩu Bánh Lốp RTG 01", "RTG", "A01", "available", "Cẩu RTG Mitsui 40 tấn - Trạng thái sẵn sàng tác nghiệp"),
                    new("RTG-02", "Cẩu Bánh Lốp RTG 02", "RTG", "A01", "working", "Đang thực hiện nâng hạ container TSK-0804") { OperatorName = "Phạm Văn Cần" },
                    new("RTG-03", "Cẩu Bánh Lốp RTG 03", "RTG", "B02", "maintenance", "Đang bảo dưỡng định kỳ hệ thống thủy lực"),
                    new("QC-01", "Cẩu Bờ Panamax QC 01", "QC", "A01", "available", "Cẩu bờ chuyên dỡ container từ tàu"),
                    new("RS-01", "Xe Nâng Chụp Reach Stacker 01", "ReachStacker", "C01", "available", "Xe nâng chụp Kalmar 45 tấn")
                };
                await _context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>().AddRangeAsync(equipments, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // 2. Seed Containers nếu chưa có
            var contCount = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>().CountAsync(cancellationToken);
            if (contCount == 0)
            {
                var containers = new List<NexusPort.Modules.Container.Domain.Entities.Container>
                {
                    new("TEMU4451920", "in_transit") { Description = "Hàng Linh Kiện Điện Tử - 40HC", SealNumber = "SEAL-VN-889922", CargoType = "dry" },
                    new("CMAU3381920", "in_transit") { Description = "Hàng May Mặc Xuất Khẩu - 20GP", SealNumber = "SEAL-VN-998811", CargoType = "dry" },
                    new("ONEU8821903", "in_transit") { Description = "Trái Cây Nhập Khẩu - 40RF", SealNumber = "SEAL-TH-445566", CargoType = "reefer" },
                    new("HLCU7719204", "in_transit") { Description = "Hóa Chất Công Nghiệp - 20TK", SealNumber = "SEAL-DE-112288", CargoType = "tank" },
                    new("MAEU5519205", "in_transit") { Description = "Thiết Bị Điện Tử Viễn Thông - 40HC", SealNumber = "SEAL-DK-990033", CargoType = "dry" },
                    new("MSCU9901123", "In_Yard") { Description = "Thủy Hải Sản Đông Lạnh (-20°C) - 40RF", SealNumber = "SEAL-RF-554411", CargoType = "reefer" },
                    new("COSU8819201", "In_Yard") { Description = "Hàng Công Nghiệp Tiêu Dùng - 40HC", SealNumber = "SEAL-COS-11223", CargoType = "dry" },
                    new("EVER1129983", "In_Yard") { Description = "Hóa Chất Đóng Can (Class 3) - 20GP", SealNumber = "SEAL-EV-778899", CargoType = "dry" }
                };
                await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>().AddRangeAsync(containers, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // 3. Seed YardTasks nếu chưa có hoặc số lượng ít hơn 6
            var count = await _context.Set<YardTask>().CountAsync(cancellationToken);
            if (count < 6)
            {
                var existingCodes = await _context.Set<YardTask>()
                    .Select(t => t.TaskCode)
                    .ToListAsync(cancellationToken);

                var sampleTasks = new List<YardTask>
                {
                    // [HÀNG CHỜ TIẾP NHẬN 1]: Test [3] Đối soát Seal & Tiếp Nhận Bãi (NXP-055)
                    new(
                        "TSK-0801",
                        "TEMU4451920",
                        "A01",
                        "Hạ Bãi Nhập",
                        "Cầu Tàu B-02",
                        "A01-04-02-1",
                        "High",
                        "Assigned",
                        "40FT HC",
                        "Hàng Linh Kiện Điện Tử",
                        "51C-889.12",
                        "Nguyễn Văn Bình",
                        DateTime.UtcNow.AddMinutes(30),
                        "Xe cont đang di chuyển từ cầu tàu về bãi A01"
                    )
                    {
                        ExpectedSealNo = "SEAL-VN-889922",
                        AssignedBy = "dispatcher01"
                    },

                    // [HÀNG CHỜ TIẾP NHẬN 2]: Test [3] Đối soát Seal & Tiếp Nhận Bãi (NXP-055)
                    new(
                        "TSK-0802",
                        "CMAU3381920",
                        "B02",
                        "Hạ Bãi Nhập",
                        "Cổng In-Gate 02",
                        "B02-02-05-2",
                        "Normal",
                        "Assigned",
                        "20FT ST",
                        "Hàng May Mặc Xuất Khẩu",
                        "50H-912.44",
                        "Trần Hoàng Nam",
                        DateTime.UtcNow.AddMinutes(45),
                        "Xe qua trạm cân Gate, đang xếp hàng chờ nhận tại bãi B02"
                    )
                    {
                        ExpectedSealNo = "SEAL-VN-998811",
                        AssignedBy = "dispatcher01"
                    },

                    // [HÀNG CHỜ TIẾP NHẬN 3]: Test [3] Đối soát cont lạnh Reefer
                    new(
                        "TSK-0806",
                        "ONEU8821903",
                        "C01",
                        "Hạ Bãi Nhập",
                        "Cầu Tàu B-01",
                        "C01-02-04-2",
                        "Critical",
                        "Assigned",
                        "40FT RF",
                        "Trái Cây Nhập Khẩu Thái Lan (-18°C)",
                        "51D-992.12",
                        "Lê Văn Cường",
                        DateTime.UtcNow.AddMinutes(20),
                        "Xe cont lạnh dỡ từ tàu ONE APUS, cần cắm điện bãi C01 khẩn"
                    )
                    {
                        ExpectedSealNo = "SEAL-TH-445566",
                        AssignedBy = "dispatcher01"
                    },

                    // [HÀNG CHỜ TIẾP NHẬN 4]: Test [3] Đối soát xe từ Cổng In-Gate
                    new(
                        "TSK-0807",
                        "HLCU7719204",
                        "B02",
                        "Hạ Bãi Nhập",
                        "Cổng In-Gate 01",
                        "B02-04-01-1",
                        "High",
                        "Assigned",
                        "20FT TANK",
                        "Hóa Chất Công Nghiệp Lỏng",
                        "60C-334.88",
                        "Vũ Văn Tuấn",
                        DateTime.UtcNow.AddMinutes(35),
                        "Xe bồn hóa chất chuyên dụng đã hoàn tất thủ tục hải quan tại cổng"
                    )
                    {
                        ExpectedSealNo = "SEAL-DE-112288",
                        AssignedBy = "dispatcher01"
                    },

                    // [HÀNG CHỜ TIẾP NHẬN 5]: Test [3] Đối soát cont máy móc
                    new(
                        "TSK-0808",
                        "MAEU5519205",
                        "A01",
                        "Hạ Bãi Nhập",
                        "Cầu Tàu B-03",
                        "A01-05-02-3",
                        "Normal",
                        "Assigned",
                        "40FT HC",
                        "Thiết Bị Điện Tử Viễn Thông",
                        "29H-772.19",
                        "Ngô Thanh Tùng",
                        DateTime.UtcNow.AddMinutes(50),
                        "Xe cont đang trên đường từ bến 03 về bãi A01"
                    )
                    {
                        ExpectedSealNo = "SEAL-DK-990033",
                        AssignedBy = "dispatcher01"
                    },

                    // GIAI ĐOẠN 2 & 4 (Bước 1): ĐÃ GÁN CẨU (Ready) -> Test [4] Bắt Đầu Cẩu (NXP-060 Bước 1)
                    new(
                        "TSK-0803",
                        "MSCU9901123",
                        "A01",
                        "Đảo Chuyển Bãi",
                        "A01-01-02-3",
                        "A01-05-01-1",
                        "Critical",
                        "Ready",
                        "40FT RF",
                        "Thủy Hải Sản Đông Lạnh (-20°C)",
                        "51D-771.88",
                        "Lê Quốc Bảo",
                        DateTime.UtcNow.AddMinutes(15),
                        "Đã kiểm tra chốt chì, cần thủ RTG-01 đã vào cabin sẵn sàng cẩu"
                    )
                    {
                        EquipmentCode = "RTG-01",
                        EquipmentType = "RTG",
                        OperatorName = "Trần Văn Hùng",
                        ExpectedSealNo = "SEAL-RF-554411",
                        ActualSealNo = "SEAL-RF-554411",
                        Condition = "Good",
                        ReceivedAt = DateTime.UtcNow.AddMinutes(-15),
                        ReceivedBy = "yard01"
                    },

                    // GIAI ĐOẠN 4 (Bước 2): ĐANG CẨU (In_Progress) -> Test [4] Hoàn Tất Tác Nghiệp Bãi (NXP-060 Bước 3)
                    new(
                        "TSK-0804",
                        "COSU8819201",
                        "A01",
                        "Xuất Bãi Giao Xe",
                        "A01-03-04-2",
                        "GATE-OUT-01",
                        "High",
                        "In_Progress",
                        "40FT HC",
                        "Hàng Công Nghiệp Tiêu Dùng",
                        "59C-334.90",
                        "Vũ Đức Thịnh",
                        DateTime.UtcNow.AddMinutes(20),
                        "Cần thủ RTG-02 đang hạ cont lên rơ-moóc xe 59C-334.90"
                    )
                    {
                        EquipmentCode = "RTG-02",
                        EquipmentType = "RTG",
                        OperatorName = "Phạm Văn Cần",
                        ExpectedSealNo = "SEAL-COS-11223",
                        ActualSealNo = "SEAL-COS-11223",
                        Condition = "Good",
                        ReceivedAt = DateTime.UtcNow.AddMinutes(-30),
                        ReceivedBy = "yard01",
                        StartTime = DateTime.UtcNow.AddMinutes(-7)
                    },

                    // ĐÃ HOÀN THÀNH (Completed)
                    new(
                        "TSK-0805",
                        "EVER1129983",
                        "B02",
                        "Hạ Bãi Nhập",
                        "Cầu Tàu B-01",
                        "B02-DG-01-1",
                        "Normal",
                        "Completed",
                        "20FT ST",
                        "Hóa Chất Đóng Can (Class 3)",
                        "60C-123.45",
                        "Đặng Văn Lâm",
                        DateTime.UtcNow.AddHours(-2),
                        "Đã hạ cont an toàn vào khu vực bãi cách ly hóa chất nguy hiểm"
                    )
                    {
                        EquipmentCode = "RTG-03",
                        EquipmentType = "RTG",
                        OperatorName = "Trần Văn Hùng",
                        ExpectedSealNo = "SEAL-EV-778899",
                        ActualSealNo = "SEAL-EV-778899",
                        Condition = "Good",
                        ReceivedAt = DateTime.UtcNow.AddHours(-2),
                        ReceivedBy = "yard01",
                        StartTime = DateTime.UtcNow.AddMinutes(-110),
                        EndTime = DateTime.UtcNow.AddMinutes(-100),
                        CompletedLocation = "B02-DG-01-1"
                    }
                };

                var tasksToAdd = sampleTasks.Where(s => !existingCodes.Contains(s.TaskCode)).ToList();
                if (tasksToAdd.Count > 0)
                {
                    await _context.Set<YardTask>().AddRangeAsync(tasksToAdd, cancellationToken);
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }

            _tableEnsured = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring yard tables and seed data");
        }
    }
}

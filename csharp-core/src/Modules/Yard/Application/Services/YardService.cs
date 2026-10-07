using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.ExternalServices;
using NexusPort.Infrastructure.Notifications.DTOs;
using NexusPort.Infrastructure.Notifications.Enums;
using NexusPort.Infrastructure.Notifications.Interfaces;
using NexusPort.Modules.Yard.Application.DTOs;
using NexusPort.Modules.Yard.Application.Interfaces;
using NexusPort.Modules.Yard.Domain.Entities;
using NexusPort.Modules.Yard.Domain.Events;

namespace NexusPort.Modules.Yard.Application.Services;

public class YardService : IYardService
{
    private readonly IYardRepository _repository;
    private readonly AppDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly IMessageBrokerService _messageBroker;
    private readonly ILogger<YardService> _logger;

    public YardService(IYardRepository repository, AppDbContext context, INotificationService notificationService, IMessageBrokerService messageBroker, ILogger<YardService> logger)
    {
        _repository = repository;
        _context = context;
        _notificationService = notificationService;
        _messageBroker = messageBroker;
        _logger = logger;
    }

    public async Task<IReadOnlyList<YardBlockDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(MapToDto).ToList();
    }

    public async Task<YardBlockDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        return entity == null ? null : MapToDto(entity);
    }

    public async Task<IReadOnlyList<YardBlockDto>> GetYardMapAsync(CancellationToken cancellationToken = default)
    {
        var blocks = await _context.Set<YardBlock>()
            .Include(b => b.Slots)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var slotIds = blocks.SelectMany(b => b.Slots).Select(s => s.Id).ToList();
        var currentPositions = await _context.Set<ContainerPosition>()
            .Where(cp => cp.IsCurrent && slotIds.Contains(cp.SlotId))
            .AsNoTracking()
            .ToDictionaryAsync(cp => cp.SlotId, cancellationToken);

        var containerIds = currentPositions.Values.Select(cp => cp.ContainerId).Distinct().ToList();
        var containerNumbers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .Where(c => containerIds.Contains(c.Id))
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.ContainerNumber, cancellationToken);

        return blocks.Select(b => new YardBlockDto
        {
            Id = b.Id,
            BlockCode = b.Code,
            Status = b.Zone ?? "Operational",
            Description = b.Name,
            MaxBays = b.Slots.Any() ? b.Slots.Max(s => s.Bay) : 10,
            MaxRows = b.Slots.Any() ? b.Slots.Max(s => s.Row) : 4,
            MaxTiers = b.Slots.Any() ? b.Slots.Max(s => s.Tier) : 5,
            CreatedAt = b.CreatedAt,
            Slots = b.Slots.Select(s => {
                var position = currentPositions.GetValueOrDefault(s.Id);
                var containerNumber = position != null && containerNumbers.TryGetValue(position.ContainerId, out var num) ? num : null;
                return new YardSlotDto
                {
                    Id = s.Id,
                    YardBlockId = s.YardBlockId,
                    Bay = s.Bay,
                    Row = s.Row,
                    Tier = s.Tier,
                    Status = s.Status,
                    ContainerId = position?.ContainerId,
                    ContainerNumber = containerNumber
                };
            }).ToList()
        }).ToList();
    }

    public async Task<IReadOnlyList<YardSlotDto>> GetBlockSlotsAsync(Guid blockId, CancellationToken cancellationToken = default)
    {
        var slots = await _context.Set<YardSlot>()
            .Where(s => s.YardBlockId == blockId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var slotIds = slots.Select(s => s.Id).ToList();
        var currentPositions = await _context.Set<ContainerPosition>()
            .Where(cp => cp.IsCurrent && slotIds.Contains(cp.SlotId))
            .AsNoTracking()
            .ToDictionaryAsync(cp => cp.SlotId, cancellationToken);

        var containerIds = currentPositions.Values.Select(cp => cp.ContainerId).Distinct().ToList();
        var containerNumbers = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .Where(c => containerIds.Contains(c.Id))
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.ContainerNumber, cancellationToken);

        return slots.Select(s => {
            var position = currentPositions.GetValueOrDefault(s.Id);
            var containerNumber = position != null && containerNumbers.TryGetValue(position.ContainerId, out var num) ? num : null;
            return new YardSlotDto
            {
                Id = s.Id,
                YardBlockId = s.YardBlockId,
                Bay = s.Bay,
                Row = s.Row,
                Tier = s.Tier,
                Status = s.Status,
                ContainerId = position?.ContainerId,
                ContainerNumber = containerNumber
            };
        }).ToList();
    }

    public async Task<bool> ToggleSlotMaintenanceAsync(Guid slotId, CancellationToken cancellationToken = default)
    {
        var slot = await _context.Set<YardSlot>().FindAsync(new object[] { slotId }, cancellationToken);
        if (slot == null)
            throw new ArgumentException("Slot not found.");

        if (slot.Status == "occupied")
            throw new InvalidOperationException("Cannot maintain an occupied slot.");

        var newStatus = slot.Status == "maintenance" ? "empty" : "maintenance";
        
        // Update via raw SQL to bypass Npgsql string-to-enum parameter type mismatch
        await _context.Database.ExecuteSqlInterpolatedAsync($"UPDATE yard_slots SET status = {newStatus}::yard_slot_status WHERE id = {slot.Id}", cancellationToken);
        
        slot.Status = newStatus;
        _context.Entry(slot).State = EntityState.Unchanged; // Prevent EF from generating UPDATE for status
        
        return true;
    }

    public async Task<bool> UpdateContainerLocationAsync(Guid containerId, Guid slotId, CancellationToken cancellationToken = default)
    {
        // Set previous position to not current
        var currentPosition = await _context.Set<ContainerPosition>()
            .FirstOrDefaultAsync(cp => cp.ContainerId == containerId && cp.IsCurrent, cancellationToken);
            
        if (currentPosition != null)
        {
            currentPosition.IsCurrent = false;
            currentPosition.RemovedAt = DateTime.UtcNow;
            
            var oldSlot = await _context.Set<YardSlot>().FindAsync(new object[] { currentPosition.SlotId }, cancellationToken);
            if (oldSlot != null) 
            {
                oldSlot.Status = "empty";
                _context.Entry(oldSlot).State = EntityState.Unchanged;
                await _context.Database.ExecuteSqlInterpolatedAsync($"UPDATE yard_slots SET status = 'empty'::yard_slot_status WHERE id = {oldSlot.Id}", cancellationToken);
            }
        }

        // Create new position
        var newSlot = await _context.Set<YardSlot>()
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);
            
        if (newSlot == null)
            throw new ArgumentException("Target slot not found.");

        newSlot.Status = "occupied";
        _context.Entry(newSlot).State = EntityState.Unchanged;
        await _context.Database.ExecuteSqlInterpolatedAsync($"UPDATE yard_slots SET status = 'occupied'::yard_slot_status WHERE id = {newSlot.Id}", cancellationToken);

        var newPosition = new ContainerPosition(containerId, slotId);
        await _context.Set<ContainerPosition>().AddAsync(newPosition, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<YardBlockDto> CreateAsync(CreateYardBlockDto dto, CancellationToken cancellationToken = default)
    {
        var existing = await _context.Set<YardBlock>().FirstOrDefaultAsync(b => b.Code == dto.BlockCode, cancellationToken);
        if (existing != null)
        {
            throw new ArgumentException($"Block with code '{dto.BlockCode}' already exists.");
        }

        var entity = new YardBlock(dto.BlockCode, dto.Description ?? "") { Zone = "Active", MaxCapacity = dto.MaxBays * dto.MaxRows * dto.MaxTiers };
        
        await _repository.AddAsync(entity, cancellationToken);

        // Insert slots using raw SQL to bypass the text to yard_slot_status cast issue in EF Core
        var sql = new System.Text.StringBuilder("INSERT INTO yard_slots (id, block_id, bay, row_no, tier, status, has_reefer_plug) VALUES ");
        var parameters = new List<object>();
        int paramIndex = 0;

        for (int bay = 1; bay <= dto.MaxBays; bay++)
        {
            for (int row = 1; row <= dto.MaxRows; row++)
            {
                for (int tier = 1; tier <= dto.MaxTiers; tier++)
                {
                    if (paramIndex > 0) sql.Append(", ");
                    sql.Append($"({{{paramIndex}}}, {{{paramIndex + 1}}}, {{{paramIndex + 2}}}, {{{paramIndex + 3}}}, {{{paramIndex + 4}}}, 'empty'::yard_slot_status, false)");
                    
                    parameters.Add(Guid.NewGuid());
                    parameters.Add(entity.Id);
                    parameters.Add(bay);
                    parameters.Add(row);
                    parameters.Add(tier);
                    
                    paramIndex += 5;
                }
            }
        }

        if (paramIndex > 0)
        {
            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }

        return MapToDto(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Delete slots first
        await _context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM yard_slots WHERE block_id = {id}", cancellationToken);
        
        // Delete block
        await _repository.DeleteAsync(id, cancellationToken);
    }

    public async Task<YardSlotDto?> ReserveSlotAsync(Guid blockId, CancellationToken cancellationToken = default)
    {
        var emptySlot = await _context.Set<YardSlot>()
            .Where(s => s.YardBlockId == blockId && s.Status == "empty")
            .OrderBy(s => s.Tier)
            .ThenBy(s => s.Row)
            .ThenBy(s => s.Bay)
            .FirstOrDefaultAsync(cancellationToken);

        if (emptySlot == null)
            return null; // No empty slots available in this block

        emptySlot.Status = "reserved";
        _context.Entry(emptySlot).State = EntityState.Unchanged; // Keep EF Core from generating normal UPDATE
        
        // Raw SQL because EF Core has issues casting to the custom enum 'yard_slot_status'
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE yard_slots SET status = 'reserved'::yard_slot_status WHERE id = {emptySlot.Id}", 
            cancellationToken);
            
        return new YardSlotDto
        {
            Id = emptySlot.Id,
            YardBlockId = emptySlot.YardBlockId,
            Bay = emptySlot.Bay,
            Row = emptySlot.Row,
            Tier = emptySlot.Tier,
            Status = "reserved"
        };
    }

    public async Task<YardSlotDto?> ReserveSpecificSlotAsync(Guid slotId, CancellationToken cancellationToken = default)
    {
        var emptySlot = await _context.Set<YardSlot>()
            .FirstOrDefaultAsync(s => s.Id == slotId && s.Status == "empty", cancellationToken);

        if (emptySlot == null)
            return null; // Slot not found or not empty

        emptySlot.Status = "reserved";
        _context.Entry(emptySlot).State = EntityState.Unchanged; // Keep EF Core from generating normal UPDATE
        
        // Raw SQL because EF Core has issues casting to the custom enum 'yard_slot_status'
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE yard_slots SET status = 'reserved'::yard_slot_status WHERE id = {emptySlot.Id}", 
            cancellationToken);
            
        return new YardSlotDto
        {
            Id = emptySlot.Id,
            YardBlockId = emptySlot.YardBlockId,
            Bay = emptySlot.Bay,
            Row = emptySlot.Row,
            Tier = emptySlot.Tier,
            Status = "reserved"
        };
    }

    public async Task<YardOperationCompletionDto> CompleteOperationAsync(Guid operationId, CompleteYardOperationDto dto, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || dto.ContainerId == Guid.Empty || dto.DriverId == Guid.Empty)
            throw new ArgumentException("OperationId, ContainerId and DriverId are required.");
        if (!string.Equals(dto.OperationStatus, "Completed", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("OperationStatus must be Completed for this endpoint.");

        // This repository currently has no EF migration runner. Ensure the
        // durable outbox table exists before recording the event.
        await _context.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS yard_operation_events (
                id uuid PRIMARY KEY,
                operation_id uuid NOT NULL,
                container_id uuid NOT NULL,
                driver_id uuid NOT NULL,
                operation_status varchar(50) NOT NULL,
                event_type varchar(100) NOT NULL,
                delivery_status varchar(20) NOT NULL,
                occurred_at timestamptz NOT NULL,
                published_at timestamptz NULL,
                delivery_error varchar(1000) NULL,
                created_at timestamptz NOT NULL
            )", cancellationToken);

        var containerExists = await _context.Set<NexusPort.Modules.Container.Domain.Entities.Container>()
            .AsNoTracking()
            .AnyAsync(container => container.Id == dto.ContainerId, cancellationToken);
        if (!containerExists)
            throw new InvalidOperationException("The target container does not exist.");

        var driverExists = await _context.Set<NexusPort.Modules.Driver.Domain.Entities.Driver>()
            .AsNoTracking()
            .AnyAsync(driver => driver.Id == dto.DriverId && driver.Status.ToString().ToLower() == "active", cancellationToken);
        if (!driverExists)
            throw new InvalidOperationException("The target driver does not exist or is inactive.");

        var eventRecord = new YardOperationEvent
        {
            OperationId = operationId,
            ContainerId = dto.ContainerId,
            DriverId = dto.DriverId,
            OperationStatus = "Completed",
            OccurredAt = DateTime.UtcNow
        };
        await _context.Set<YardOperationEvent>().AddAsync(eventRecord, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        await _notificationService.SendAsync(new SendNotificationDto
        {
            RecipientId = dto.DriverId,
            Type = NotificationType.YardOperationCompleted,
            Severity = NotificationSeverity.Success,
            Title = "Yard operation completed",
            Message = $"Container {dto.ContainerId} has completed its yard operation.",
            ReferenceId = operationId.ToString()
        }, cancellationToken);

        var integrationEvent = new YardOperationCompletedEvent(eventRecord.Id, operationId, dto.ContainerId, dto.DriverId, eventRecord.OperationStatus, eventRecord.OccurredAt);
        try
        {
            await _messageBroker.PublishAsync(YardOperationCompletedEvent.EventName, integrationEvent, cancellationToken);
            eventRecord.MarkPublished();
        }
        catch (Exception exception)
        {
            eventRecord.MarkFailed(exception.Message);
            _logger.LogError(exception, "Unable to publish yard completion event {EventId}", eventRecord.Id);
        }
        await _context.SaveChangesAsync(cancellationToken);

        return new YardOperationCompletionDto
        {
            EventId = eventRecord.Id,
            OperationId = operationId,
            ContainerId = dto.ContainerId,
            DriverId = dto.DriverId,
            OperationStatus = eventRecord.OperationStatus,
            DeliveryStatus = eventRecord.DeliveryStatus,
            CompletedAt = eventRecord.OccurredAt
        };
    }

    private static YardBlockDto MapToDto(YardBlock entity) => new()
    {
        Id = entity.Id,
        BlockCode = entity.Code,
        Status = entity.Zone ?? "Operational",
        Description = entity.Name,
        CreatedAt = entity.CreatedAt
    };
}

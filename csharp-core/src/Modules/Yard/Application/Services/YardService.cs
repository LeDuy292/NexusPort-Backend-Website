using Microsoft.EntityFrameworkCore;
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

    public async Task<YardBlockDto> CreateAsync(CreateYardBlockDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new YardBlock { BlockCode = dto.BlockCode, Description = dto.Description, Status = "Active" };
        await _repository.AddAsync(entity, cancellationToken);
        return MapToDto(entity);
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
            .AnyAsync(driver => driver.Id == dto.DriverId && driver.Status == "active", cancellationToken);
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
        BlockCode = entity.BlockCode,
        Status = entity.Status,
        Description = entity.Description,
        CreatedAt = entity.CreatedAt
    };
}

using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.Notifications.DTOs;
using NexusPort.Infrastructure.Notifications.Entities;
using NexusPort.Infrastructure.Notifications.Interfaces;
using NexusPort.Shared.Results;

namespace NexusPort.Infrastructure.Notifications.Services;

public class NotificationService : INotificationService
{
    private readonly AppDbContext _context;

    public NotificationService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<NotificationDto> SendAsync(SendNotificationDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new Notification(
            dto.RecipientId,
            dto.Type,
            dto.Title,
            dto.Message,
            dto.Severity,
            dto.ReferenceId
        );

        await _context.Set<Notification>().AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(entity);
    }

    public async Task<PagedResult<NotificationDto>> GetUserNotificationsAsync(
        Guid recipientId,
        NotificationFilterParams filter,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Set<Notification>()
            .AsNoTracking()
            .Where(n => n.RecipientId == recipientId);

        if (filter.UnreadOnly.HasValue && filter.UnreadOnly.Value)
        {
            query = query.Where(n => !n.IsRead);
        }

        if (filter.Type.HasValue)
        {
            query = query.Where(n => n.Type == filter.Type.Value);
        }

        if (filter.Severity.HasValue)
        {
            query = query.Where(n => n.Severity == filter.Severity.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var pageNumber = filter.PageNumber <= 0 ? 1 : filter.PageNumber;
        var pageSize = filter.PageSize <= 0 ? 10 : filter.PageSize;

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var dtos = items.Select(MapToDto).ToList();
        return new PagedResult<NotificationDto>(dtos, totalCount, pageNumber, pageSize);
    }

    public async Task<int> GetUnreadCountAsync(Guid recipientId, CancellationToken cancellationToken = default)
    {
        return await _context.Set<Notification>()
            .AsNoTracking()
            .CountAsync(n => n.RecipientId == recipientId && !n.IsRead, cancellationToken);
    }

    public async Task<bool> MarkAsReadAsync(Guid notificationId, Guid recipientId, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Set<Notification>()
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientId == recipientId, cancellationToken);

        if (entity == null) return false;

        entity.MarkAsRead();
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MarkAllAsReadAsync(Guid recipientId, CancellationToken cancellationToken = default)
    {
        var unreadNotifications = await _context.Set<Notification>()
            .Where(n => n.RecipientId == recipientId && !n.IsRead)
            .ToListAsync(cancellationToken);

        if (!unreadNotifications.Any()) return true;

        foreach (var notification in unreadNotifications)
        {
            notification.MarkAsRead();
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static NotificationDto MapToDto(Notification entity)
    {
        return new NotificationDto
        {
            Id = entity.Id,
            RecipientId = entity.RecipientId,
            Type = entity.Type,
            Severity = entity.Severity,
            Title = entity.Title,
            Message = entity.Message,
            IsRead = entity.IsRead,
            ReadAt = entity.ReadAt,
            ReferenceId = entity.ReferenceId,
            CreatedAt = entity.CreatedAt
        };
    }
}

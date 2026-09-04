using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Shared.Results;

namespace NexusPort.Modules.Booking.Infrastructure.Repositories;

public class BookingRepository : IBookingRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<Domain.Entities.Booking> _dbSet;

    public BookingRepository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<Domain.Entities.Booking>();
    }

    public async Task<Domain.Entities.Booking?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(b => b.BookingContainers)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<Domain.Entities.Booking?> GetByIdWithContainersAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(b => b.BookingContainers)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Entities.Booking>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(b => b.BookingContainers)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<Domain.Entities.Booking>> GetPagedAsync(BookingFilterParams filter, CancellationToken cancellationToken = default)
    {
        var query = _dbSet
            .Include(b => b.BookingContainers)
            .AsNoTracking()
            .AsQueryable();

        if (filter.CarrierId.HasValue && filter.CarrierId.Value != Guid.Empty)
        {
            query = query.Where(b => b.CarrierId == filter.CarrierId.Value);
        }

        if (filter.Status.HasValue)
        {
            query = query.Where(b => b.Status == filter.Status.Value);
        }

        if (filter.BookingType.HasValue)
        {
            query = query.Where(b => b.BookingType == filter.BookingType.Value);
        }

        if (filter.DateFrom.HasValue)
        {
            query = query.Where(b => b.AppointmentStart >= filter.DateFrom.Value);
        }

        if (filter.DateTo.HasValue)
        {
            query = query.Where(b => b.AppointmentEnd <= filter.DateTo.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var searchTerm = filter.Search.Trim().ToLower();
            query = query.Where(b => b.BookingCode.ToLower().Contains(searchTerm));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var pageNumber = filter.PageNumber <= 0 ? 1 : filter.PageNumber;
        var pageSize = filter.PageSize <= 0 ? 10 : filter.PageSize;

        var items = await query
            .OrderByDescending(b => b.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Domain.Entities.Booking>(items, totalCount, pageNumber, pageSize);
    }

    public async Task AddAsync(Domain.Entities.Booking entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Domain.Entities.Booking entity, CancellationToken cancellationToken = default)
    {
        _dbSet.Update(entity);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await GetByIdAsync(id, cancellationToken);
        if (entity != null)
        {
            _dbSet.Remove(entity);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}

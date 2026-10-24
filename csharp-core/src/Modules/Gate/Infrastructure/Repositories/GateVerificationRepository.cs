using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Gate.Application.DTOs;
using NexusPort.Modules.Gate.Application.Interfaces;
using NexusPort.Modules.Gate.Domain.Entities;

namespace NexusPort.Modules.Gate.Infrastructure.Repositories;

public class GateVerificationRepository : IGateVerificationRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<GateVerificationRecord> _dbSet;

    public GateVerificationRepository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<GateVerificationRecord>();
    }

    public async Task<GateVerificationRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<GateVerificationRecord?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.VerificationCode == code, cancellationToken);
    }

    public async Task<IReadOnlyList<GateVerificationRecord>> GetListAsync(GateVerificationFilterDto filter, CancellationToken cancellationToken = default)
    {
        var query = BuildFilterQuery(filter);

        return await query
            .OrderByDescending(x => x.VerificationTime)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(GateVerificationFilterDto filter, CancellationToken cancellationToken = default)
    {
        var query = BuildFilterQuery(filter);
        return await query.CountAsync(cancellationToken);
    }

    public async Task AddAsync(GateVerificationRecord entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(GateVerificationRecord entity, CancellationToken cancellationToken = default)
    {
        _dbSet.Update(entity);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<GateVerificationRecord> BuildFilterQuery(GateVerificationFilterDto filter)
    {
        var query = _dbSet.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.GateCode))
        {
            var gate = filter.GateCode.Trim().ToUpper();
            query = query.Where(x => x.GateCode.ToUpper() == gate);
        }

        if (!string.IsNullOrWhiteSpace(filter.VerificationStatus))
        {
            var status = filter.VerificationStatus.Trim().ToUpper();
            query = query.Where(x => x.VerificationStatus.ToUpper() == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.PlateNumber))
        {
            var plate = filter.PlateNumber.Trim().ToUpper();
            query = query.Where(x => x.DetectedPlate.ToUpper().Contains(plate) || (x.VehiclePlate != null && x.VehiclePlate.ToUpper().Contains(plate)));
        }

        if (!string.IsNullOrWhiteSpace(filter.BookingNumber))
        {
            var bkg = filter.BookingNumber.Trim().ToUpper();
            query = query.Where(x => x.BookingNumber != null && x.BookingNumber.ToUpper().Contains(bkg));
        }

        if (filter.FromDate.HasValue)
        {
            query = query.Where(x => x.VerificationTime >= filter.FromDate.Value);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(x => x.VerificationTime <= filter.ToDate.Value);
        }

        return query;
    }
}

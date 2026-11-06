using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Driver.Application.DTOs;
using NexusPort.Modules.Driver.Application.Interfaces;

namespace NexusPort.Modules.Driver.Infrastructure.Repositories;

public class DriverRepository : IDriverRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<NexusPort.Modules.Driver.Domain.Entities.Driver> _dbSet;

    public DriverRepository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<NexusPort.Modules.Driver.Domain.Entities.Driver>();
    }

    public async Task<NexusPort.Modules.Driver.Domain.Entities.Driver?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var driver = await _dbSet.FindAsync(new object[] { id }, cancellationToken);
        if (driver != null && driver.LicenseExpiryDate.HasValue && driver.Status != NexusPort.Modules.Driver.Domain.Enums.DriverStatus.banned)
        {
            var threshold = DateTime.UtcNow.AddDays(15);
            if (driver.LicenseExpiryDate.Value <= threshold)
            {
                driver.Status = NexusPort.Modules.Driver.Domain.Enums.DriverStatus.banned;
                await _context.SaveChangesAsync(cancellationToken);
            }
        }
        return driver;
    }

    public async Task<IReadOnlyList<NexusPort.Modules.Driver.Domain.Entities.Driver>> GetAllAsync(DriverFilterDto filter, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var threshold = now.AddDays(15);
        
        var expiringDrivers = await _dbSet
            .Where(d => d.LicenseExpiryDate.HasValue && d.LicenseExpiryDate.Value <= threshold && d.Status != NexusPort.Modules.Driver.Domain.Enums.DriverStatus.banned)
            .ToListAsync(cancellationToken);
            
        if (expiringDrivers.Any())
        {
            foreach (var d in expiringDrivers)
            {
                d.Status = NexusPort.Modules.Driver.Domain.Enums.DriverStatus.banned;
            }
            await _context.SaveChangesAsync(cancellationToken);
        }

        var query = _dbSet.AsQueryable();

        if (filter.CarrierId.HasValue)
        {
            query = query.Where(x => x.CarrierId == filter.CarrierId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (Enum.TryParse<NexusPort.Modules.Driver.Domain.Enums.DriverStatus>(filter.Status, true, out var parsedStatus))
            {
                query = query.Where(x => x.Status == parsedStatus);
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var search = filter.SearchTerm.ToLower();
            query = query.Where(x => x.FullName.ToLower().Contains(search) || 
                                     x.LicenseNumber.ToLower().Contains(search) || 
                                     (x.Phone != null && x.Phone.Contains(search)));
        }

        return await query.OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(NexusPort.Modules.Driver.Domain.Entities.Driver entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(NexusPort.Modules.Driver.Domain.Entities.Driver entity, CancellationToken cancellationToken = default)
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

    public async Task<bool> ExistsByLicenseAsync(Guid carrierId, string licenseNumber, Guid? excludeDriverId = null, CancellationToken cancellationToken = default)
    {
        var query = _dbSet.Where(x => x.CarrierId == carrierId && x.LicenseNumber == licenseNumber);
        
        if (excludeDriverId.HasValue)
        {
            query = query.Where(x => x.Id != excludeDriverId.Value);
        }

        return await query.AnyAsync(cancellationToken);
    }

    public async Task UnassignVehiclesFromDriverAsync(Guid driverId, CancellationToken cancellationToken = default)
    {
        await _context.Database.ExecuteSqlRawAsync("UPDATE trucks SET driver_id = NULL WHERE driver_id = {0}", new object[] { driverId }, cancellationToken);
    }
}

using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Vehicle.Application.Interfaces;

namespace NexusPort.Modules.Vehicle.Infrastructure.Repositories;

public class VehicleRepository : IVehicleRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle> _dbSet;

    public VehicleRepository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>();
    }

    public async Task<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IReadOnlyList<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet.ToListAsync(cancellationToken);
    }

    public async Task AddAsync(NexusPort.Modules.Vehicle.Domain.Entities.Vehicle entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(NexusPort.Modules.Vehicle.Domain.Entities.Vehicle entity, CancellationToken cancellationToken = default)
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

using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Vessel.Application.Interfaces;

namespace NexusPort.Modules.Vessel.Infrastructure.Repositories;

public class VesselRepository : IVesselRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<NexusPort.Modules.Vessel.Domain.Entities.Vessel> _dbSet;

    public VesselRepository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<NexusPort.Modules.Vessel.Domain.Entities.Vessel>();
    }

    public async Task<NexusPort.Modules.Vessel.Domain.Entities.Vessel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IReadOnlyList<NexusPort.Modules.Vessel.Domain.Entities.Vessel>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet.ToListAsync(cancellationToken);
    }

    public async Task AddAsync(NexusPort.Modules.Vessel.Domain.Entities.Vessel entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(NexusPort.Modules.Vessel.Domain.Entities.Vessel entity, CancellationToken cancellationToken = default)
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

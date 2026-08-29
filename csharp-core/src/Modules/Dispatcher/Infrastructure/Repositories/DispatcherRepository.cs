using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Dispatcher.Application.Interfaces;

namespace NexusPort.Modules.Dispatcher.Infrastructure.Repositories;

public class DispatcherRepository : IDispatcherRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder> _dbSet;

    public DispatcherRepository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder>();
    }

    public async Task<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IReadOnlyList<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet.ToListAsync(cancellationToken);
    }

    public async Task AddAsync(NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder entity, CancellationToken cancellationToken = default)
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

using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Container.Application.Interfaces;

namespace NexusPort.Modules.Container.Infrastructure.Repositories;

public class ContainerRepository : IContainerRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<NexusPort.Modules.Container.Domain.Entities.Container> _dbSet;

    public ContainerRepository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<NexusPort.Modules.Container.Domain.Entities.Container>();
    }

    public async Task<NexusPort.Modules.Container.Domain.Entities.Container?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IReadOnlyList<NexusPort.Modules.Container.Domain.Entities.Container>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet.ToListAsync(cancellationToken);
    }

    public async Task AddAsync(NexusPort.Modules.Container.Domain.Entities.Container entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(NexusPort.Modules.Container.Domain.Entities.Container entity, CancellationToken cancellationToken = default)
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

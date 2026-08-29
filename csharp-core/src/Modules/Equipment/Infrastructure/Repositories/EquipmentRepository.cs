using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Equipment.Application.Interfaces;

namespace NexusPort.Modules.Equipment.Infrastructure.Repositories;

public class EquipmentRepository : IEquipmentRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<NexusPort.Modules.Equipment.Domain.Entities.Equipment> _dbSet;

    public EquipmentRepository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>();
    }

    public async Task<NexusPort.Modules.Equipment.Domain.Entities.Equipment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IReadOnlyList<NexusPort.Modules.Equipment.Domain.Entities.Equipment>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet.ToListAsync(cancellationToken);
    }

    public async Task AddAsync(NexusPort.Modules.Equipment.Domain.Entities.Equipment entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(NexusPort.Modules.Equipment.Domain.Entities.Equipment entity, CancellationToken cancellationToken = default)
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

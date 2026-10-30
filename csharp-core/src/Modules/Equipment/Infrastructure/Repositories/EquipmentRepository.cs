using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Equipment.Application.Interfaces;

namespace NexusPort.Modules.Equipment.Infrastructure.Repositories;

public class EquipmentRepository : IEquipmentRepository
{
    private readonly AppDbContext _context;
    private readonly DbSet<NexusPort.Modules.Equipment.Domain.Entities.Equipment> _dbSet;
    private static bool _tableEnsured = false;

    public EquipmentRepository(AppDbContext context)
    {
        _context = context;
        _dbSet = context.Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>();
    }

    private async Task EnsureTableCreatedAsync(CancellationToken cancellationToken = default)
    {
        if (_tableEnsured) return;
        const string sql = @"
CREATE TABLE IF NOT EXISTS ""Equipments"" (
    ""Id"" uuid PRIMARY KEY,
    ""EquipmentCode"" varchar(100) NOT NULL,
    ""Name"" varchar(150),
    ""EquipmentType"" varchar(50),
    ""Status"" varchar(50) NOT NULL,
    ""BlockCode"" varchar(50),
    ""OperatorId"" uuid,
    ""OperatorName"" varchar(150),
    ""Description"" varchar(500),
    ""CreatedAt"" timestamptz NOT NULL DEFAULT now(),
    ""CreatedBy"" varchar(150),
    ""UpdatedAt"" timestamptz,
    ""UpdatedBy"" varchar(150),
    ""IsDeleted"" boolean NOT NULL DEFAULT false
);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""CreatedBy"" varchar(150);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""UpdatedBy"" varchar(150);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""IsDeleted"" boolean NOT NULL DEFAULT false;
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""EquipmentType"" varchar(50);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""BlockCode"" varchar(50);
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""OperatorId"" uuid;
ALTER TABLE ""Equipments"" ADD COLUMN IF NOT EXISTS ""OperatorName"" varchar(150);
CREATE INDEX IF NOT EXISTS ix_equipments_code ON ""Equipments"" (""EquipmentCode"");
CREATE INDEX IF NOT EXISTS ix_equipments_blockcode ON ""Equipments"" (""BlockCode"");
CREATE INDEX IF NOT EXISTS ix_equipments_status ON ""Equipments"" (""Status"");
";
        try
        {
            await _context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            _tableEnsured = true;
        }
        catch
        {
            // Ignore if already created or permission issue
        }
    }

    public async Task<NexusPort.Modules.Equipment.Domain.Entities.Equipment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureTableCreatedAsync(cancellationToken);
        return await _dbSet.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IReadOnlyList<NexusPort.Modules.Equipment.Domain.Entities.Equipment>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTableCreatedAsync(cancellationToken);
        return await _dbSet.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NexusPort.Modules.Equipment.Domain.Entities.Equipment>> GetAvailableByBlockAsync(string? blockCode, CancellationToken cancellationToken = default)
    {
        await EnsureTableCreatedAsync(cancellationToken);
        var query = _dbSet.Where(e => e.Status.ToLower() == "available" || e.Status.ToLower() == "active" || e.Status.ToLower() == "ready" || e.Status.ToLower() == "idle");
        if (!string.IsNullOrWhiteSpace(blockCode))
        {
            query = query.Where(e => e.BlockCode.ToLower() == blockCode.ToLower());
        }
        return await query.ToListAsync(cancellationToken);
    }

    public async Task AddAsync(NexusPort.Modules.Equipment.Domain.Entities.Equipment entity, CancellationToken cancellationToken = default)
    {
        await EnsureTableCreatedAsync(cancellationToken);
        await _dbSet.AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(NexusPort.Modules.Equipment.Domain.Entities.Equipment entity, CancellationToken cancellationToken = default)
    {
        await EnsureTableCreatedAsync(cancellationToken);
        _dbSet.Update(entity);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureTableCreatedAsync(cancellationToken);
        var entity = await GetByIdAsync(id, cancellationToken);
        if (entity != null)
        {
            _dbSet.Remove(entity);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}

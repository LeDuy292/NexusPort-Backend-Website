using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Equipment.Infrastructure.Persistence;

public class EquipmentDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Equipment.Domain.Entities.Equipment> Equipments => Set<NexusPort.Modules.Equipment.Domain.Entities.Equipment>();

    public EquipmentDbContext(DbContextOptions<EquipmentDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EquipmentDbContext).Assembly);
    }
}

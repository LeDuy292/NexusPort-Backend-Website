using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Berth.Infrastructure.Persistence;

public class BerthDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Berth.Domain.Entities.Berth> Berths => Set<NexusPort.Modules.Berth.Domain.Entities.Berth>();

    public BerthDbContext(DbContextOptions<BerthDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BerthDbContext).Assembly);
    }
}

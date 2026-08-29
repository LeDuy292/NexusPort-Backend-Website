using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Yard.Infrastructure.Persistence;

public class YardDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Yard.Domain.Entities.YardBlock> YardBlocks => Set<NexusPort.Modules.Yard.Domain.Entities.YardBlock>();

    public YardDbContext(DbContextOptions<YardDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(YardDbContext).Assembly);
    }
}

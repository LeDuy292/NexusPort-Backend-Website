using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Container.Infrastructure.Persistence;

public class ContainerDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Container.Domain.Entities.Container> Containers => Set<NexusPort.Modules.Container.Domain.Entities.Container>();

    public ContainerDbContext(DbContextOptions<ContainerDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ContainerDbContext).Assembly);
    }
}

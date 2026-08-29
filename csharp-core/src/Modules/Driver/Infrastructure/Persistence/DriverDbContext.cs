using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Driver.Infrastructure.Persistence;

public class DriverDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Driver.Domain.Entities.Driver> Drivers => Set<NexusPort.Modules.Driver.Domain.Entities.Driver>();

    public DriverDbContext(DbContextOptions<DriverDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DriverDbContext).Assembly);
    }
}

using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Gate.Infrastructure.Persistence;

public class GateDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Gate.Domain.Entities.GateTransaction> GateTransactions => Set<NexusPort.Modules.Gate.Domain.Entities.GateTransaction>();

    public GateDbContext(DbContextOptions<GateDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GateDbContext).Assembly);
    }
}

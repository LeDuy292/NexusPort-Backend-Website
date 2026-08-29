using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Dispatcher.Infrastructure.Persistence;

public class DispatcherDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder> WorkOrders => Set<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder>();

    public DispatcherDbContext(DbContextOptions<DispatcherDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DispatcherDbContext).Assembly);
    }
}

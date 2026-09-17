using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NexusPort.Shared.Kernel;

namespace NexusPort.Infrastructure.Database;

public class AppDbContext : DbContext
{
    public static readonly List<Assembly> ModuleAssemblies = new();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresEnum("driver_status", new[] { "active", "inactive", "banned" });
        modelBuilder.HasPostgresEnum("truck_status", new[] { "active", "inactive", "maintenance" });
        modelBuilder.HasPostgresEnum("booking_type", new[] { "pickup", "dropoff" });
        modelBuilder.HasPostgresEnum("booking_status", new[] { "pending", "ready", "approved", "rejected", "canceled", "checked_in", "completed", "expired" });
        modelBuilder.HasPostgresEnum("container_status", new[] { "expected", "discharged", "in_yard", "reserved", "moving", "gate_in", "gate_out", "loaded", "damaged", "canceled" });
        modelBuilder.HasPostgresEnum("cargo_type", new[] { "general", "dangerous", "refrigerated", "out_of_gauge", "liquid_bulk", "dry_bulk", "vehicles", "livestock", "empty" });


        foreach (var assembly in ModuleAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.FullName != null && a.FullName.StartsWith("NexusPort.Modules")))
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = DateTime.UtcNow;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                    break;
            }
        }

        return await base.SaveChangesAsync(cancellationToken);
    }
}

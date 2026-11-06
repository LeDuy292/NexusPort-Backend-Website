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
        modelBuilder.HasPostgresEnum("driver_status", new[] { "active", "inactive", "banned", "waiting_confirmation", "receiving_vehicle", "vehicle_received", "booking_confirmed", "transporting", "transport_completed" });
        modelBuilder.HasPostgresEnum("truck_status", new[] { "active", "inactive", "maintenance" });
        modelBuilder.HasPostgresEnum("booking_type", new[] { "pickup", "dropoff" });
        modelBuilder.HasPostgresEnum("yard_slot_status", new[] { "empty", "reserved", "occupied", "maintenance" });
        modelBuilder.HasPostgresEnum("booking_status", new[] { "pending", "ready", "approved", "rejected", "canceled", "checked_in", "completed", "expired" });
        modelBuilder.HasPostgresEnum("container_status", new[] { "expected", "discharged", "in_yard", "reserved", "moving", "gate_in", "gate_out", "loaded", "damaged", "canceled" });
        modelBuilder.HasPostgresEnum("cargo_type", new[] { "general", "dangerous", "refrigerated", "out_of_gauge", "liquid_bulk", "dry_bulk", "vehicles", "livestock", "empty" });
        modelBuilder.HasPostgresEnum("reefer_request_status", new[] { "PENDING_ASSIGNMENT", "ASSIGNED", "IN_PROGRESS", "COMPLETED", "CANCELLED", "REJECTED" });
        modelBuilder.HasPostgresEnum("socket_usage_status", new[] { "AVAILABLE", "RESERVED", "OCCUPIED" });
        modelBuilder.HasPostgresEnum("socket_technical_status", new[] { "HEALTHY", "FAULTY", "UNDER_MAINTENANCE", "DECOMMISSIONED" });
        modelBuilder.HasPostgresEnum("power_session_status", new[] { "AWAITING_POWER", "ACTIVE", "SUSPENDED", "COMPLETED", "TRANSFERRED", "CANCELLED" });
        modelBuilder.HasPostgresEnum("actual_power_status", new[] { "UNKNOWN", "POWERED_ON", "POWER_LOST", "POWERED_OFF" });
        modelBuilder.HasPostgresEnum("temp_data_source", new[] { "MANUAL_INSPECTION", "IOT_TELEMATICS", "PLC_GATEWAY" });
        modelBuilder.HasPostgresEnum("alert_severity", new[] { "INFO", "WARNING", "CRITICAL" });
        modelBuilder.HasPostgresEnum("reefer_alert_status", new[] { "TRIGGERED", "ACKNOWLEDGED", "IN_PROGRESS", "RESOLVED", "DISMISSED" });

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


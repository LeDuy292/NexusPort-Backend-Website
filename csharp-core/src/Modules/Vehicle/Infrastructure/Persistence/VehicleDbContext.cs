using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Vehicle.Infrastructure.Persistence;

public class VehicleDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle> Vehicles => Set<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>();

    public VehicleDbContext(DbContextOptions<VehicleDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VehicleDbContext).Assembly);
    }
}

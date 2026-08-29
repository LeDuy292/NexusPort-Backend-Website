using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Vessel.Infrastructure.Persistence;

public class VesselDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Vessel.Domain.Entities.Vessel> Vessels => Set<NexusPort.Modules.Vessel.Domain.Entities.Vessel>();

    public VesselDbContext(DbContextOptions<VesselDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VesselDbContext).Assembly);
    }
}

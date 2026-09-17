using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Carrier.Domain.Entities;

namespace NexusPort.Modules.Carrier.Infrastructure.Persistence;

public class CarrierDbContext : DbContext
{
    public DbSet<CarrierEntity> Carriers { get; set; } = null!;

    public CarrierDbContext(DbContextOptions<CarrierDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<NexusPort.Modules.Carrier.Domain.Enums.CompanyStatus>();
        modelBuilder.ApplyConfiguration(new CarrierConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}

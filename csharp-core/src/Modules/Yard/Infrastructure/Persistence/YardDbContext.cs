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

        // Ánh xạ tường minh các Entity liên module để EF Core trỏ đúng bảng PostgreSQL
        modelBuilder.Entity<NexusPort.Modules.Container.Domain.Entities.Container>(b =>
        {
            b.ToTable("Containers");
            b.HasKey(x => x.Id);
            b.Property(x => x.ContainerNumber).IsRequired().HasMaxLength(100);
            b.Property(x => x.Status).IsRequired().HasMaxLength(50);
            b.Property(x => x.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<NexusPort.Modules.Equipment.Domain.Entities.Equipment>(b =>
        {
            b.ToTable("Equipments");
            b.HasKey(x => x.Id);
            b.Property(x => x.EquipmentCode).IsRequired().HasMaxLength(100);
            b.Property(x => x.Name).HasMaxLength(150);
            b.Property(x => x.EquipmentType).HasMaxLength(50);
            b.Property(x => x.Status).IsRequired().HasMaxLength(50);
            b.Property(x => x.BlockCode).HasMaxLength(50);
            b.Property(x => x.OperatorName).HasMaxLength(150);
            b.Property(x => x.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<NexusPort.Modules.Driver.Domain.Entities.Driver>(b =>
        {
            b.ToTable("drivers");
            b.HasKey(x => x.Id);
        });
    }
}

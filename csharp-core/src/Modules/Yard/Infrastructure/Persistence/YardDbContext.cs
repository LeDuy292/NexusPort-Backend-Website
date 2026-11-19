using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Yard.Infrastructure.Persistence;

public class YardDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Yard.Domain.Entities.YardBlock> YardBlocks => Set<NexusPort.Modules.Yard.Domain.Entities.YardBlock>();
    public DbSet<NexusPort.Modules.Yard.Domain.Entities.YardSlot> YardSlots => Set<NexusPort.Modules.Yard.Domain.Entities.YardSlot>();
    public DbSet<NexusPort.Modules.Yard.Domain.Entities.ContainerPosition> ContainerPositions => Set<NexusPort.Modules.Yard.Domain.Entities.ContainerPosition>();
    public DbSet<NexusPort.Modules.Yard.Preparation.Domain.PreparationTask> PreparationTasks => Set<NexusPort.Modules.Yard.Preparation.Domain.PreparationTask>();
    public DbSet<NexusPort.Modules.Yard.Preparation.Domain.PreparationWorkOrder> PreparationWorkOrders => Set<NexusPort.Modules.Yard.Preparation.Domain.PreparationWorkOrder>();
    public DbSet<NexusPort.Modules.Yard.Preparation.Domain.SlotReservation> SlotReservations => Set<NexusPort.Modules.Yard.Preparation.Domain.SlotReservation>();
    public DbSet<NexusPort.Modules.Yard.Preparation.Domain.PreparationConfig> PreparationConfigs => Set<NexusPort.Modules.Yard.Preparation.Domain.PreparationConfig>();
    public DbSet<NexusPort.Modules.Yard.Preparation.Domain.ContainerMovement> ContainerMovements => Set<NexusPort.Modules.Yard.Preparation.Domain.ContainerMovement>();
    public DbSet<NexusPort.Modules.Yard.Preparation.Domain.EDoRecord> EDoRecords => Set<NexusPort.Modules.Yard.Preparation.Domain.EDoRecord>();
    public DbSet<NexusPort.Modules.Yard.Preparation.Domain.ExceptionItem> ExceptionItems => Set<NexusPort.Modules.Yard.Preparation.Domain.ExceptionItem>();
    public DbSet<NexusPort.Modules.Yard.Preparation.Domain.PreparationOutboxMessage> PreparationOutboxMessages => Set<NexusPort.Modules.Yard.Preparation.Domain.PreparationOutboxMessage>();

    public YardDbContext(DbContextOptions<YardDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(YardDbContext).Assembly);

        // Ánh xạ tường minh các Entity liên module để EF Core trỏ đúng bảng PostgreSQL
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

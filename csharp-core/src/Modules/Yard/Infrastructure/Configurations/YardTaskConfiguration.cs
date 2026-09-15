using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Yard.Domain.Entities;

namespace NexusPort.Modules.Yard.Infrastructure.Configurations;

public class YardTaskConfiguration : IEntityTypeConfiguration<YardTask>
{
    public void Configure(EntityTypeBuilder<YardTask> builder)
    {
        builder.ToTable("yard_tasks");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TaskCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.OperationType).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ContainerNo).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ContainerType).HasMaxLength(50);
        builder.Property(x => x.CargoType).HasMaxLength(150);
        builder.Property(x => x.VehiclePlate).HasMaxLength(50);
        builder.Property(x => x.DriverName).HasMaxLength(150);
        builder.Property(x => x.FromLocation).HasMaxLength(100);
        builder.Property(x => x.ToLocation).HasMaxLength(100);
        builder.Property(x => x.BlockCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.EquipmentCode).HasMaxLength(50);
        builder.Property(x => x.EquipmentType).HasMaxLength(50);
        builder.Property(x => x.OperatorName).HasMaxLength(150);
        builder.Property(x => x.Priority).IsRequired().HasMaxLength(30);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.AssignedBy).HasMaxLength(150);
        builder.Property(x => x.Notes).HasMaxLength(500);

        builder.HasIndex(x => x.TaskCode);
        builder.HasIndex(x => x.BlockCode);
        builder.HasIndex(x => x.Status);
    }
}

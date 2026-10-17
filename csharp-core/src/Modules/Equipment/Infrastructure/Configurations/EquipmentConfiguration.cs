using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Equipment.Infrastructure.Configurations;

public class EquipmentConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Equipment.Domain.Entities.Equipment>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Equipment.Domain.Entities.Equipment> builder)
    {
        builder.ToTable("Equipments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EquipmentCode).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Name).HasMaxLength(150);
        builder.Property(x => x.EquipmentType).HasMaxLength(50);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.BlockCode).HasMaxLength(50);
        builder.Property(x => x.OperatorName).HasMaxLength(150);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}


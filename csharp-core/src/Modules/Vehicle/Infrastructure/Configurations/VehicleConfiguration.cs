using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Vehicle.Infrastructure.Configurations;

public class VehicleConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle> builder)
    {
        builder.ToTable("Vehicles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PlateNumber).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

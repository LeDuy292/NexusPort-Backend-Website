using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Vessel.Infrastructure.Configurations;

public class VesselConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Vessel.Domain.Entities.Vessel>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Vessel.Domain.Entities.Vessel> builder)
    {
        builder.ToTable("Vessels");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.VesselName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

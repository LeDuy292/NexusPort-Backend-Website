using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Berth.Infrastructure.Configurations;

public class BerthConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Berth.Domain.Entities.Berth>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Berth.Domain.Entities.Berth> builder)
    {
        builder.ToTable("Berths");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BerthCode).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

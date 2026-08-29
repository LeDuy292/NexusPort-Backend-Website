using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Driver.Infrastructure.Configurations;

public class DriverConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Driver.Domain.Entities.Driver>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Driver.Domain.Entities.Driver> builder)
    {
        builder.ToTable("Drivers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FullName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

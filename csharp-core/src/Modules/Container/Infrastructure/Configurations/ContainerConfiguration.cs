using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Container.Infrastructure.Configurations;

public class ContainerConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Container.Domain.Entities.Container>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Container.Domain.Entities.Container> builder)
    {
        builder.ToTable("Containers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ContainerNumber).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

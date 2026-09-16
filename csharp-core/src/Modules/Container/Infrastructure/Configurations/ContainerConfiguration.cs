using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Container.Infrastructure.Configurations;

public class ContainerConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Container.Domain.Entities.Container>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Container.Domain.Entities.Container> builder)
    {
        builder.ToTable("containers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.CarrierId).HasColumnName("carrier_id");
        builder.Property(x => x.ContainerNumber).HasColumnName("container_no").IsRequired().HasMaxLength(30);
        builder.Ignore(x => x.SealNumber);
        builder.Ignore(x => x.CargoType);
        builder.Ignore(x => x.Status);
        builder.Ignore(x => x.Description);
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Container.Infrastructure.Configurations;

public class ContainerConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Container.Domain.Entities.Container>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Container.Domain.Entities.Container> builder)
    {
        builder.ToTable("containers");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.CarrierId)
            .HasColumnName("carrier_id");

        builder.Property(x => x.ContainerNumber)
            .HasColumnName("container_no")
            .IsRequired()
            .HasMaxLength(30);

        builder.Property(x => x.SealNumber)
            .HasColumnName("seal_no")
            .HasMaxLength(50);

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("container_status")
            .IsRequired();

        builder.Property(x => x.CargoType)
            .HasColumnName("cargo_type")
            .HasColumnType("cargo_type");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at");

        builder.Ignore(x => x.Description);
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

    }
}

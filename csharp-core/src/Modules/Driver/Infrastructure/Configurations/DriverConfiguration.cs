using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Driver.Infrastructure.Configurations;

public class DriverConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Driver.Domain.Entities.Driver>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Driver.Domain.Entities.Driver> builder)
    {
        builder.ToTable("drivers");
        builder.HasKey(x => x.Id);
        
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.CarrierId).HasColumnName("carrier_id").IsRequired();
        builder.Property(x => x.FullName).HasColumnName("full_name").IsRequired().HasMaxLength(150);
        builder.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(30);
        builder.Property(x => x.IdCardNumber).HasColumnName("id_card_number").HasMaxLength(50);
        builder.Property(x => x.LicenseNumber).HasColumnName("license_number").IsRequired().HasMaxLength(80);
        
        builder.Property(x => x.Status)
            .HasColumnName("status")
            .IsRequired();
            
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        // Unique index
        builder.HasIndex(x => new { x.CarrierId, x.LicenseNumber })
            .IsUnique()
            .HasDatabaseName("ux_drivers_carrier_id_license_number");
    }
}

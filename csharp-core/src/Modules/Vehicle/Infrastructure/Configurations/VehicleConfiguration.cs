using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NexusPort.Modules.Vehicle.Domain.Enums;

namespace NexusPort.Modules.Vehicle.Infrastructure.Configurations;

public class VehicleConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Vehicle.Domain.Entities.Vehicle> builder)
    {
        // Maps to actual "trucks" table in PostgreSQL
        builder.ToTable("trucks");
        builder.HasKey(x => x.Id);
        
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.CarrierId).HasColumnName("carrier_id");
        builder.Property(x => x.DriverId).HasColumnName("driver_id");
        builder.Property(x => x.PlateNumber).HasColumnName("plate_number").IsRequired().HasMaxLength(30);
        builder.Property(x => x.RfidTag).HasColumnName("rfid_tag").HasMaxLength(100);
        builder.Property(x => x.VehicleType).HasColumnName("vehicle_type").HasMaxLength(80);
        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("truck_status")
            .IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        // Trucks table does not have these columns — ignore them
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.IsDeleted);
        builder.Ignore(x => x.Description);

        builder.HasIndex(x => x.PlateNumber).IsUnique();
    }
}

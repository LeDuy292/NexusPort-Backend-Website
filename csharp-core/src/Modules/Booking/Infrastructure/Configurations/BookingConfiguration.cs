using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Booking.Infrastructure.Configurations;

public class BookingConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Booking.Domain.Entities.Booking>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Booking.Domain.Entities.Booking> builder)
    {
        builder.ToTable("Bookings");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BookingNumber).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);

        builder.Property(x => x.VehiclePlate).HasMaxLength(50);
        builder.Property(x => x.DriverName).HasMaxLength(200);
        builder.Property(x => x.GateType).HasMaxLength(50);

        builder.HasIndex(x => x.BookingNumber).IsUnique();
        builder.HasIndex(x => x.VehiclePlate);
        builder.HasIndex(x => x.Status);
    }
}

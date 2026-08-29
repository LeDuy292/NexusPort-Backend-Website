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
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Booking.Domain.Entities;

namespace NexusPort.Modules.Booking.Infrastructure.Configurations;

public class BookingContainerConfiguration : IEntityTypeConfiguration<BookingContainer>
{
    public void Configure(EntityTypeBuilder<BookingContainer> builder)
    {
        builder.ToTable("booking_containers");

        builder.HasKey(x => new { x.BookingId, x.ContainerId });

        builder.Property(x => x.BookingId)
            .HasColumnName("booking_id")
            .IsRequired();

        builder.Property(x => x.ContainerId)
            .HasColumnName("container_id")
            .IsRequired();

        builder.Property(x => x.Note)
            .HasColumnName("note");

        builder.HasOne(x => x.Booking)
            .WithMany(x => x.BookingContainers)
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

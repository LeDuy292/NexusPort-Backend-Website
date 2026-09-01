using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Booking.Infrastructure.Configurations;

public class BookingConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Booking.Domain.Entities.Booking>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Booking.Domain.Entities.Booking> builder)
    {
        builder.ToTable("bookings");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.CarrierId)
            .HasColumnName("carrier_id")
            .IsRequired();

        builder.Property(x => x.DriverId)
            .HasColumnName("driver_id");

        builder.Property(x => x.TruckId)
            .HasColumnName("truck_id");

        builder.Property(x => x.BookingCode)
            .HasColumnName("booking_code")
            .IsRequired()
            .HasMaxLength(80);

        builder.HasIndex(x => x.BookingCode)
            .IsUnique();

        builder.Property(x => x.BookingType)
            .HasColumnName("booking_type")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.AppointmentStart)
            .HasColumnName("appointment_start")
            .IsRequired();

        builder.Property(x => x.AppointmentEnd)
            .HasColumnName("appointment_end")
            .IsRequired();

        builder.Property(x => x.ApprovedBy)
            .HasColumnName("approved_by");

        builder.Property(x => x.ApprovedAt)
            .HasColumnName("approved_at");

        builder.Property(x => x.RejectedReason)
            .HasColumnName("rejected_reason");

        builder.Property(x => x.CanceledAt)
            .HasColumnName("canceled_at");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.CreatedBy)
            .HasColumnName("created_by");

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at");

        builder.Property(x => x.UpdatedBy)
            .HasColumnName("updated_by");

        builder.Property(x => x.IsDeleted)
            .HasColumnName("is_deleted");

        builder.HasMany(x => x.BookingContainers)
            .WithOne(x => x.Booking)
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

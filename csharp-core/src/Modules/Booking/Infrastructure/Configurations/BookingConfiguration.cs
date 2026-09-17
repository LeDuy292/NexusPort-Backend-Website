using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Booking.Domain.Enums;

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
            .HasColumnType("booking_type")
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("booking_status")
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

        builder.Ignore(x => x.CreatedBy);

        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        // Ignore unmapped helper properties for DB-first schema compatibility
        builder.Ignore(x => x.BookingNumber);
        builder.Ignore(x => x.VehiclePlate);
        builder.Ignore(x => x.VehicleId);
        builder.Ignore(x => x.DriverName);
        builder.Ignore(x => x.ValidFrom);
        builder.Ignore(x => x.ValidTo);
        builder.Ignore(x => x.GateType);
        builder.Ignore(x => x.Description);

        builder.HasMany(x => x.BookingContainers)
            .WithOne(x => x.Booking)
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

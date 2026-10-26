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
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
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
        builder.Ignore(x => x.RequiredPowerKw);
        builder.Property(x => x.VehiclePlate).HasColumnName("vehicle_plate");
        builder.Property(x => x.VehicleId).HasColumnName("vehicle_id");
        builder.Property(x => x.DriverName).HasColumnName("driver_name");
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from");
        builder.Property(x => x.ValidTo).HasColumnName("valid_to");
        builder.Property(x => x.GateType).HasColumnName("gate_type");
        builder.Property(x => x.Description).HasColumnName("description");
        builder.Property(x => x.IsReeferPowerRequired).HasColumnName("is_reefer_power_required");
        builder.Property(x => x.TargetTemperature).HasColumnName("target_temperature");
        builder.Property(x => x.TemperatureUnit).HasColumnName("temperature_unit");
        builder.Property(x => x.TempTolerance).HasColumnName("temp_tolerance");
        builder.Property(x => x.ReeferPlugId).HasColumnName("reefer_plug_id");
        builder.Property(x => x.ReeferPlugCode).HasColumnName("reefer_plug_code");
        builder.Property(x => x.ReeferPowerStatus).HasColumnName("reefer_power_status");
        builder.Property(x => x.IsCombined).HasColumnName("is_combined");
        builder.Property(x => x.TripStatus).HasColumnName("trip_status");
        builder.Property(x => x.CustomerId).HasColumnName("customer_id");

        builder.HasMany(x => x.BookingContainers)
            .WithOne(x => x.Booking)
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.BookingOperations)
            .WithOne(x => x.Booking)
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.BookingDocuments)
            .WithOne(x => x.Booking)
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

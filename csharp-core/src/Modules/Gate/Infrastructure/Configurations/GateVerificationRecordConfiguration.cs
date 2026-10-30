using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Gate.Domain.Entities;

namespace NexusPort.Modules.Gate.Infrastructure.Configurations;

public class GateVerificationRecordConfiguration : IEntityTypeConfiguration<GateVerificationRecord>
{
    public void Configure(EntityTypeBuilder<GateVerificationRecord> builder)
    {
        builder.ToTable("gate_verification_records");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.VerificationCode)
            .HasColumnName("verification_code")
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.LaneCode)
            .HasColumnName("lane_code")
            .HasMaxLength(50);

        builder.Property(x => x.VerificationType)
            .HasColumnName("verification_type")
            .HasConversion<string>()
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.VerificationStatus)
            .HasColumnName("status")
            .HasConversion<string>()
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(100);

        builder.Property(x => x.DetectedPlate)
            .HasColumnName("detected_plate")
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.PlateConfidence)
            .HasColumnName("plate_confidence");

        builder.Property(x => x.RfidTag)
            .HasColumnName("rfid_tag")
            .HasMaxLength(100);

        builder.Property(x => x.VehicleDetected)
            .HasColumnName("vehicle_detected");

        builder.Property(x => x.CameraId)
            .HasColumnName("camera_id")
            .HasMaxLength(100);

        builder.Property(x => x.BookingId)
            .HasColumnName("booking_id");

        builder.Property(x => x.VehicleId)
            .HasColumnName("truck_id");

        builder.Property(x => x.DriverId)
            .HasColumnName("driver_id");

        builder.Property(x => x.VehiclePlateImageUrl)
            .HasColumnName("plate_image_url");

        builder.Property(x => x.OverviewImageUrl)
            .HasColumnName("overview_image_url");

        builder.Property(x => x.Notes)
            .HasColumnName("notes")
            .HasMaxLength(1000);

        builder.Property(x => x.VerificationTime)
            .HasColumnName("verified_at");

        // Ignore unmapped fields
        builder.Property(x => x.GateCode).HasColumnName("gate_code").HasMaxLength(50).IsRequired();
        builder.Property(x => x.BookingNumber).HasColumnName("booking_number");
        builder.Property(x => x.VehiclePlate).HasColumnName("vehicle_plate");
        builder.Property(x => x.DriverName).HasColumnName("driver_name");
        builder.Property(x => x.ProcessedBy).HasColumnName("processed_by");
        builder.Property(x => x.OcrRawData).HasColumnName("ocr_raw_data");
        builder.Ignore(x => x.CreatedAt);
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        // Indexes for high performance querying
        builder.HasIndex(x => x.VerificationCode).IsUnique();
        builder.HasIndex(x => x.DetectedPlate);
        builder.HasIndex(x => x.RfidTag);
        builder.HasIndex(x => x.VerificationTime);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Gate.Domain.Entities;

namespace NexusPort.Modules.Gate.Infrastructure.Configurations;

public class GateVerificationRecordConfiguration : IEntityTypeConfiguration<GateVerificationRecord>
{
    public void Configure(EntityTypeBuilder<GateVerificationRecord> builder)
    {
        builder.ToTable("GateVerificationRecords");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.VerificationCode)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.GateCode)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.LaneCode)
            .HasMaxLength(50);

        builder.Property(x => x.VerificationType)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.VerificationStatus)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.FailureReason)
            .HasMaxLength(100);

        builder.Property(x => x.DetectedPlate)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.CameraId)
            .HasMaxLength(100);

        builder.Property(x => x.BookingNumber)
            .HasMaxLength(100);

        builder.Property(x => x.VehiclePlate)
            .HasMaxLength(50);

        builder.Property(x => x.DriverName)
            .HasMaxLength(200);

        builder.Property(x => x.VehiclePlateImageUrl)
            .HasMaxLength(1000);

        builder.Property(x => x.OverviewImageUrl)
            .HasMaxLength(1000);

        builder.Property(x => x.Notes)
            .HasMaxLength(1000);

        builder.Property(x => x.ProcessedBy)
            .HasMaxLength(100);

        builder.Property(x => x.RfidTag)
            .HasMaxLength(100);

        // Indexes for high performance querying
        builder.HasIndex(x => x.VerificationCode).IsUnique();
        builder.HasIndex(x => x.GateCode);
        builder.HasIndex(x => x.VerificationStatus);
        builder.HasIndex(x => x.DetectedPlate);
        builder.HasIndex(x => x.RfidTag);
        builder.HasIndex(x => x.BookingNumber);
        builder.HasIndex(x => x.VerificationTime);
    }
}

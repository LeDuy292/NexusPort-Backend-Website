using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Booking.Domain.Entities;

namespace NexusPort.Modules.Booking.Infrastructure.Configurations;

public class BookingOperationConfiguration : IEntityTypeConfiguration<BookingOperation>
{
    public void Configure(EntityTypeBuilder<BookingOperation> builder)
    {
        builder.ToTable("booking_operations");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(x => x.OperationType).HasColumnName("operation_type").IsRequired().HasMaxLength(50);
        builder.Property(x => x.SequenceNumber).HasColumnName("sequence_number").IsRequired();
        builder.Property(x => x.ExpectedGateDirection).HasColumnName("expected_gate_direction").IsRequired().HasMaxLength(20);
        builder.Property(x => x.Status).HasColumnName("status").IsRequired().HasMaxLength(50);

        builder.Property(x => x.ContainerId).HasColumnName("container_id");
        builder.Property(x => x.ContainerNumber).HasColumnName("container_number").IsRequired().HasMaxLength(20);
        builder.Property(x => x.ContainerIsoSize).HasColumnName("container_iso_size").HasMaxLength(10);
        builder.Property(x => x.CargoStatus).HasColumnName("cargo_status").IsRequired().HasMaxLength(20);
        builder.Property(x => x.CargoType).HasColumnName("cargo_type").IsRequired().HasMaxLength(50);

        builder.Property(x => x.CarrierBookingNumber).HasColumnName("carrier_booking_number").HasMaxLength(100);
        builder.Property(x => x.BillOfLadingNumber).HasColumnName("bill_of_lading_number").HasMaxLength(100);
        builder.Property(x => x.DeliveryOrderNumber).HasColumnName("delivery_order_number").HasMaxLength(100);
        builder.Property(x => x.CustomsDeclarationNumber).HasColumnName("customs_declaration_number").HasMaxLength(100);
        builder.Property(x => x.SealNumber).HasColumnName("seal_number").HasMaxLength(50);
        builder.Property(x => x.GrossWeightKg).HasColumnName("gross_weight_kg");

        builder.Property(x => x.TemperatureSetpoint).HasColumnName("temperature_setpoint");
        builder.Property(x => x.TemperatureUnit).HasColumnName("temperature_unit").HasMaxLength(5);
        builder.Property(x => x.UnNumber).HasColumnName("un_number").HasMaxLength(20);
        builder.Property(x => x.DgClass).HasColumnName("dg_class").HasMaxLength(20);
        builder.Property(x => x.SpecialHandlingRequirements).HasColumnName("special_handling_requirements");

        builder.Property(x => x.VerificationStatus).HasColumnName("verification_status").IsRequired().HasMaxLength(50);
        builder.Property(x => x.VerifiedBy).HasColumnName("verified_by");
        builder.Property(x => x.VerifiedAt).HasColumnName("verified_at");
        builder.Property(x => x.VerificationNotes).HasColumnName("verification_notes");

        builder.Property(x => x.EirNumber).HasColumnName("eir_number").HasMaxLength(100);
        builder.Property(x => x.EirIssuedAt).HasColumnName("eir_issued_at");
        builder.Property(x => x.ContainerCondition).HasColumnName("container_condition").HasMaxLength(100);

        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        builder.HasOne(x => x.Booking)
            .WithMany(x => x.BookingOperations)
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Documents)
            .WithOne(x => x.BookingOperation)
            .HasForeignKey(x => x.BookingOperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

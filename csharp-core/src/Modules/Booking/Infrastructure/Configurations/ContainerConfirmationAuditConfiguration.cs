using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Booking.Infrastructure.Configurations;

public class ContainerConfirmationAuditConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Booking.Domain.Entities.ContainerConfirmationAudit>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Booking.Domain.Entities.ContainerConfirmationAudit> builder)
    {
        builder.ToTable("container_confirmation_audits");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(x => x.ContainerId).HasColumnName("container_id").IsRequired();
        builder.Property(x => x.DriverId).HasColumnName("driver_id").IsRequired();
        builder.Property(x => x.Condition).HasColumnName("condition").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(1000);
        builder.Property(x => x.ContainerStatusAfter).HasColumnName("container_status_after").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.BookingId, x.ContainerId }).IsUnique();
    }
}

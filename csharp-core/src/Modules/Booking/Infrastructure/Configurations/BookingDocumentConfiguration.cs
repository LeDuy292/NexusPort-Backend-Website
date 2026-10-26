using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Booking.Domain.Entities;

namespace NexusPort.Modules.Booking.Infrastructure.Configurations;

public class BookingDocumentConfiguration : IEntityTypeConfiguration<BookingDocument>
{
    public void Configure(EntityTypeBuilder<BookingDocument> builder)
    {
        builder.ToTable("booking_documents");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(x => x.BookingOperationId).HasColumnName("booking_operation_id");
        builder.Property(x => x.DocumentType).HasColumnName("document_type").IsRequired().HasMaxLength(80);
        builder.Property(x => x.DocumentNumber).HasColumnName("document_number").HasMaxLength(100);
        builder.Property(x => x.FileUrl).HasColumnName("file_url").HasMaxLength(500);
        builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255);
        builder.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes");
        builder.Property(x => x.Issuer).HasColumnName("issuer").HasMaxLength(100);

        builder.Property(x => x.VerificationStatus).HasColumnName("verification_status").IsRequired().HasMaxLength(50);
        builder.Property(x => x.VerifiedBy).HasColumnName("verified_by");
        builder.Property(x => x.VerifiedAt).HasColumnName("verified_at");
        builder.Property(x => x.RejectionReason).HasColumnName("rejection_reason");
        builder.Property(x => x.ExpiryDate).HasColumnName("expiry_date");

        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        builder.HasOne(x => x.Booking)
            .WithMany(x => x.BookingDocuments)
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.BookingOperation)
            .WithMany(x => x.Documents)
            .HasForeignKey(x => x.BookingOperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

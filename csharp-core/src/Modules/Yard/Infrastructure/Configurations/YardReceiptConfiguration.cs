using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Yard.Domain.Entities;

namespace NexusPort.Modules.Yard.Infrastructure.Configurations;

public class YardReceiptConfiguration : IEntityTypeConfiguration<YardReceipt>
{
    public void Configure(EntityTypeBuilder<YardReceipt> builder)
    {
        builder.ToTable("yard_receipts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContainerId).HasColumnName("container_id").IsRequired();
        builder.Property(x => x.ContainerNo).HasColumnName("container_no").HasMaxLength(50).IsRequired();
        builder.Property(x => x.ExpectedSealNo).HasColumnName("expected_seal_no").HasMaxLength(50);
        builder.Property(x => x.ActualSealNo).HasColumnName("actual_seal_no").HasMaxLength(50);
        builder.Property(x => x.IsSealIntact).HasColumnName("is_seal_intact").IsRequired();
        builder.Property(x => x.IsMatchingContainer).HasColumnName("is_matching_container").IsRequired();
        builder.Property(x => x.Condition).HasColumnName("condition").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(500);
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
        builder.Property(x => x.ReceivedBy).HasColumnName("received_by").HasMaxLength(100).IsRequired();
        builder.Property(x => x.InspectorName).HasColumnName("inspector_name").HasMaxLength(100).IsRequired();
        builder.Property(x => x.YardBlockCode).HasColumnName("yard_block_code").HasMaxLength(20).IsRequired();
        builder.Property(x => x.LocationCoordinate).HasColumnName("location_coordinate").HasMaxLength(50).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        builder.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        
        builder.HasIndex(x => x.ContainerNo);
        builder.HasIndex(x => x.ReceivedAt);
    }
}

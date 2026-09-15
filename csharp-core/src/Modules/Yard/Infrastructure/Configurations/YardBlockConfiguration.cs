using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Yard.Infrastructure.Configurations;

public class YardBlockConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Yard.Domain.Entities.YardBlock>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Yard.Domain.Entities.YardBlock> builder)
    {
        builder.ToTable("YardBlocks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BlockCode).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

public class YardOperationEventConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Yard.Domain.Entities.YardOperationEvent>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Yard.Domain.Entities.YardOperationEvent> builder)
    {
        builder.ToTable("yard_operation_events");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OperationId).HasColumnName("operation_id").IsRequired();
        builder.Property(x => x.ContainerId).HasColumnName("container_id").IsRequired();
        builder.Property(x => x.DriverId).HasColumnName("driver_id").IsRequired();
        builder.Property(x => x.OperationStatus).HasColumnName("operation_status").HasMaxLength(50).IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.DeliveryStatus).HasColumnName("delivery_status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(x => x.PublishedAt).HasColumnName("published_at");
        builder.Property(x => x.DeliveryError).HasColumnName("delivery_error").HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.DriverId, x.OccurredAt });
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);
    }
}

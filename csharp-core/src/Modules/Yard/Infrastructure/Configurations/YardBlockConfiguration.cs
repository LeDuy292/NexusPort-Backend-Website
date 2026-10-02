using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Yard.Infrastructure.Configurations;

public class YardBlockConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Yard.Domain.Entities.YardBlock>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Yard.Domain.Entities.YardBlock> builder)
    {
        builder.ToTable("yard_blocks");
        builder.HasKey(x => x.Id);
        
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Code).HasColumnName("code").IsRequired().HasMaxLength(50);
        builder.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(150);
        builder.Property(x => x.Zone).HasColumnName("zone").HasMaxLength(80);
        
        builder.Property(x => x.MaxCapacity).HasColumnName("max_capacity").IsRequired();
        builder.Property(x => x.CurrentOccupancy).HasColumnName("current_occupancy").IsRequired();
        
        builder.Property(x => x.IsReeferArea).HasColumnName("is_reefer_area").IsRequired();
        builder.Property(x => x.IsDangerousArea).HasColumnName("is_dangerous_area").IsRequired();
        builder.Property(x => x.IsOversizedArea).HasColumnName("is_oversized_area").IsRequired();
        builder.Property(x => x.IsNearGate).HasColumnName("is_near_gate").IsRequired();
        
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        
        // Ignore BaseEntity properties that don't exist in the database table
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        builder.HasMany(x => x.Slots)
               .WithOne(s => s.Block)
               .HasForeignKey(s => s.YardBlockId)
               .OnDelete(DeleteBehavior.Cascade);
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

public class YardSlotConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Yard.Domain.Entities.YardSlot>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Yard.Domain.Entities.YardSlot> builder)
    {
        builder.ToTable("yard_slots");
        builder.HasKey(x => x.Id);
        
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.YardBlockId).HasColumnName("block_id").IsRequired();
        
        builder.Property(x => x.Bay).HasColumnName("bay").IsRequired();
        builder.Property(x => x.Row).HasColumnName("row_no").IsRequired();
        builder.Property(x => x.Tier).HasColumnName("tier").IsRequired();
        
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("yard_slot_status").IsRequired();
        builder.Property(x => x.MaxWeightKg).HasColumnName("max_weight_kg");
        builder.Property(x => x.HasReeferPlug).HasColumnName("has_reefer_plug").IsRequired();
        
        // Ignore base properties
        builder.Ignore(x => x.CreatedAt);
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        // Ensure a slot coordinate is unique within a block
        builder.HasIndex(x => new { x.YardBlockId, x.Bay, x.Row, x.Tier }).IsUnique();
    }
}

public class ContainerPositionConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Yard.Domain.Entities.ContainerPosition>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Yard.Domain.Entities.ContainerPosition> builder)
    {
        builder.ToTable("container_positions");
        builder.HasKey(x => x.Id);
        
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ContainerId).HasColumnName("container_id").IsRequired();
        builder.Property(x => x.SlotId).HasColumnName("slot_id").IsRequired();
        builder.Property(x => x.PlacedBy).HasColumnName("placed_by");
        builder.Property(x => x.PlacedAt).HasColumnName("placed_at").IsRequired();
        builder.Property(x => x.RemovedAt).HasColumnName("removed_at");
        builder.Property(x => x.IsCurrent).HasColumnName("is_current").IsRequired();

        // Ignore base properties
        builder.Ignore(x => x.CreatedAt);
        builder.Ignore(x => x.CreatedBy);
        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.UpdatedBy);
        builder.Ignore(x => x.IsDeleted);

        builder.HasOne(x => x.Slot)
               .WithMany()
               .HasForeignKey(x => x.SlotId);
    }
}

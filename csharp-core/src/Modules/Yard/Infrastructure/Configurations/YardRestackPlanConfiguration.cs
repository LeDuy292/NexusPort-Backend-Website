using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Yard.Domain.Entities;

namespace NexusPort.Modules.Yard.Infrastructure.Configurations;

public class YardRestackPlanConfiguration : IEntityTypeConfiguration<YardRestackPlan>
{
    public void Configure(EntityTypeBuilder<YardRestackPlan> builder)
    {
        builder.ToTable("yard_restack_plans");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PlanCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.TargetContainerNo).IsRequired().HasMaxLength(50);
        builder.Property(x => x.TargetContainerType).HasMaxLength(50);
        builder.Property(x => x.TargetLocation).IsRequired().HasMaxLength(100);
        builder.Property(x => x.BlockCode).IsRequired().HasMaxLength(50);
        builder.Property(x => x.TargetDestination).IsRequired().HasMaxLength(150);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.AssignedEquipmentCode).HasMaxLength(50);
        builder.Property(x => x.AssignedOperatorName).HasMaxLength(150);
        builder.Property(x => x.CreatedBy).HasMaxLength(150);
        builder.Property(x => x.EstimatedFee).HasPrecision(12, 2);
        builder.Property(x => x.Notes).HasMaxLength(500);

        builder.HasIndex(x => x.PlanCode).IsUnique();
        builder.HasIndex(x => x.TargetContainerNo);
        builder.HasIndex(x => x.Status);

        builder.HasMany(x => x.Steps)
            .WithOne(x => x.Plan)
            .HasForeignKey(x => x.PlanId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

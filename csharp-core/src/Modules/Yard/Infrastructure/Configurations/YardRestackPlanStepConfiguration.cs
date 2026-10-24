using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusPort.Modules.Yard.Domain.Entities;

namespace NexusPort.Modules.Yard.Infrastructure.Configurations;

public class YardRestackPlanStepConfiguration : IEntityTypeConfiguration<YardRestackPlanStep>
{
    public void Configure(EntityTypeBuilder<YardRestackPlanStep> builder)
    {
        builder.ToTable("yard_restack_plan_steps");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ContainerNo).IsRequired().HasMaxLength(50);
        builder.Property(x => x.ContainerType).HasMaxLength(50);
        builder.Property(x => x.StepType).IsRequired().HasMaxLength(50);
        builder.Property(x => x.FromLocation).IsRequired().HasMaxLength(100);
        builder.Property(x => x.ToLocation).IsRequired().HasMaxLength(150);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.EquipmentCode).HasMaxLength(50);
        builder.Property(x => x.OperatorName).HasMaxLength(150);
        builder.Property(x => x.Fee).HasPrecision(12, 2);
        builder.Property(x => x.Reason).HasMaxLength(250);

        builder.HasIndex(x => x.PlanId);
        builder.HasIndex(x => x.ContainerNo);
        builder.HasIndex(x => x.Status);
    }
}

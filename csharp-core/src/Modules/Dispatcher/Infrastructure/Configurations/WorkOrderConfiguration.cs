using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Dispatcher.Infrastructure.Configurations;

public class WorkOrderConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Dispatcher.Domain.Entities.WorkOrder> builder)
    {
        builder.ToTable("WorkOrders");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OrderNumber).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

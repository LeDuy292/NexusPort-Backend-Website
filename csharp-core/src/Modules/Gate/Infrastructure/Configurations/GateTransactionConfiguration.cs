using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Gate.Infrastructure.Configurations;

public class GateTransactionConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Gate.Domain.Entities.GateTransaction>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Gate.Domain.Entities.GateTransaction> builder)
    {
        builder.ToTable("GateTransactions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TransactionCode).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

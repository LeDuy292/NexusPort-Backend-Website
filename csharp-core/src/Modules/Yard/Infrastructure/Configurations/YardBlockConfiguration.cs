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

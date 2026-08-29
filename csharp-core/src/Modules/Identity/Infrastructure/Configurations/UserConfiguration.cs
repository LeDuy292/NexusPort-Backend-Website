using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NexusPort.Modules.Identity.Infrastructure.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<NexusPort.Modules.Identity.Domain.Entities.User>
{
    public void Configure(EntityTypeBuilder<NexusPort.Modules.Identity.Domain.Entities.User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Username).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

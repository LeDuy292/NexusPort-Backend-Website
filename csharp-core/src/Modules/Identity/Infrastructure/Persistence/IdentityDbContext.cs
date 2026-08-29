using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Identity.Infrastructure.Persistence;

public class IdentityDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Identity.Domain.Entities.User> Users => Set<NexusPort.Modules.Identity.Domain.Entities.User>();

    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
    }
}

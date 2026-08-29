using Microsoft.EntityFrameworkCore;

namespace NexusPort.Modules.Booking.Infrastructure.Persistence;

public class BookingDbContext : DbContext
{
    public DbSet<NexusPort.Modules.Booking.Domain.Entities.Booking> Bookings => Set<NexusPort.Modules.Booking.Domain.Entities.Booking>();

    public BookingDbContext(DbContextOptions<BookingDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BookingDbContext).Assembly);
    }
}

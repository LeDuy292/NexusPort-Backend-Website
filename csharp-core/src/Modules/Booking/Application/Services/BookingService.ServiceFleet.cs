using NexusPort.Modules.Booking.Application.DTOs;

namespace NexusPort.Modules.Booking.Application.Services;

/// <summary>Read-only availability shared with service registration; creates no transport booking.</summary>
public interface IBookingFleetAvailability
{
    Task<BookingFleetOptions> GetFleetAsync(Guid company, DateTime start, DateTime end, CancellationToken ct);
}

public partial class BookingService : IBookingFleetAvailability
{
    public Task<BookingFleetOptions> GetFleetAsync(Guid company, DateTime start, DateTime end, CancellationToken ct)
        => LoadBookingFleetAsync(company, start, end, null, ct);
}

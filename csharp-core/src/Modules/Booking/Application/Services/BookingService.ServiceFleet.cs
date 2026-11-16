using NexusPort.Modules.Booking.Application.DTOs;

namespace NexusPort.Modules.Booking.Application.Services;

/// <summary>Read-only availability shared with service registration; creates no transport booking.</summary>
public interface IBookingFleetAvailability
{
    Task<BookingFleetOptions> GetFleetAsync(Guid company, DateTime start, DateTime end, CancellationToken ct);
    Task<BookingFleetOptions> GetFleetAsync(Guid company, DateTime start, DateTime end, Guid? excludeDraftId, CancellationToken ct)
        => GetFleetAsync(company, start, end, ct);
}

public partial class BookingService : IBookingFleetAvailability
{
    public Task<BookingFleetOptions> GetFleetAsync(Guid company, DateTime start, DateTime end, CancellationToken ct)
        => LoadBookingFleetAsync(company, start, end, null, ct);

    public Task<BookingFleetOptions> GetFleetAsync(Guid company, DateTime start, DateTime end, Guid? excludeDraftId, CancellationToken ct)
        => LoadBookingFleetAsync(company, start, end, excludeDraftId, ct);
}

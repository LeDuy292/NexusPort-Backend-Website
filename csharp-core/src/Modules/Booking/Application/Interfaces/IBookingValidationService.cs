using NexusPort.Modules.Booking.Application.DTOs;

namespace NexusPort.Modules.Booking.Application.Interfaces;

public interface IBookingValidationService
{
    Task ValidateBookingAsync(CreateBookingDto dto, CancellationToken cancellationToken = default);
}

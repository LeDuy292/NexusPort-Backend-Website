namespace NexusPort.Modules.Booking.Application.Interfaces;

public interface IBookingRepository
{
    Task<NexusPort.Modules.Booking.Domain.Entities.Booking?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Booking.Domain.Entities.Booking>> GetAllAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NexusPort.Modules.Booking.Domain.Entities.Booking entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(NexusPort.Modules.Booking.Domain.Entities.Booking entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IBookingService
{
    Task<IReadOnlyList<DTOs.BookingDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DTOs.BookingDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DTOs.BookingDto> CreateAsync(DTOs.CreateBookingDto dto, CancellationToken cancellationToken = default);
}

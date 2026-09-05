using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Shared.Results;

namespace NexusPort.Modules.Booking.Application.Interfaces;

public interface IBookingRepository
{
    Task<Domain.Entities.Booking?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Domain.Entities.Booking?> GetByIdWithContainersAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Domain.Entities.Booking>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<PagedResult<Domain.Entities.Booking>> GetPagedAsync(BookingFilterParams filter, CancellationToken cancellationToken = default);
    Task AddAsync(Domain.Entities.Booking entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(Domain.Entities.Booking entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IBookingService
{
    Task<IReadOnlyList<BookingDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<PagedResult<BookingDto>> GetPagedAsync(BookingFilterParams filter, CancellationToken cancellationToken = default);
    Task<BookingDto?> GetByIdAsync(Guid id, Guid? userCarrierId = null, CancellationToken cancellationToken = default);
    Task<BookingDto> CreateAsync(CreateBookingDto dto, CancellationToken cancellationToken = default);
    Task<BookingDto> UpdateAsync(Guid id, UpdateBookingDto dto, Guid? userCarrierId = null, CancellationToken cancellationToken = default);
    Task<BookingDto> CancelAsync(Guid id, CancelBookingDto dto, Guid? userCarrierId = null, CancellationToken cancellationToken = default);
}

using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;

namespace NexusPort.Modules.Booking.Application.Services;

public class BookingService : IBookingService
{
    private readonly IBookingRepository _repository;

    public BookingService(IBookingRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<BookingDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _repository.GetAllAsync(cancellationToken);
        return entities.Select(e => new BookingDto
        {
            Id = e.Id,
            BookingNumber = e.BookingNumber,
            Status = e.Status,
            Description = e.Description,
            CreatedAt = e.CreatedAt
        }).ToList();
    }

    public async Task<BookingDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity == null) return null;
        return new BookingDto
        {
            Id = entity.Id,
            BookingNumber = entity.BookingNumber,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<BookingDto> CreateAsync(CreateBookingDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Booking.Domain.Entities.Booking
        {
            BookingNumber = dto.BookingNumber,
            Description = dto.Description,
            Status = "Active"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return new BookingDto
        {
            Id = entity.Id,
            BookingNumber = entity.BookingNumber,
            Status = entity.Status,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt
        };
    }
}

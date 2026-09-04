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
        return entities.Select(MapToDto).ToList();
    }

    public async Task<BookingDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        return entity == null ? null : MapToDto(entity);
    }

    public async Task<BookingDto> CreateAsync(CreateBookingDto dto, CancellationToken cancellationToken = default)
    {
        var entity = new NexusPort.Modules.Booking.Domain.Entities.Booking
        {
            BookingNumber = dto.BookingNumber,
            Description = dto.Description,
            Status = string.IsNullOrWhiteSpace(dto.Status) ? "Active" : dto.Status,
            VehiclePlate = dto.VehiclePlate,
            VehicleId = dto.VehicleId,
            DriverName = dto.DriverName,
            DriverId = dto.DriverId,
            ValidFrom = dto.ValidFrom,
            ValidTo = dto.ValidTo,
            GateType = dto.GateType ?? "GateIn"
        };
        await _repository.AddAsync(entity, cancellationToken);
        return MapToDto(entity);
    }

    private static BookingDto MapToDto(NexusPort.Modules.Booking.Domain.Entities.Booking entity)
    {
        return new BookingDto
        {
            Id = entity.Id,
            BookingNumber = entity.BookingNumber,
            Status = entity.Status,
            Description = entity.Description,
            VehiclePlate = entity.VehiclePlate,
            VehicleId = entity.VehicleId,
            DriverName = entity.DriverName,
            DriverId = entity.DriverId,
            ValidFrom = entity.ValidFrom,
            ValidTo = entity.ValidTo,
            GateType = entity.GateType,
            CreatedAt = entity.CreatedAt
        };
    }
}

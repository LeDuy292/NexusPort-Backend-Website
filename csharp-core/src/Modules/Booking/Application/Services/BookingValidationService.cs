using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public class BookingValidationService : IBookingValidationService
{
    private readonly AppDbContext _context;

    public BookingValidationService(AppDbContext context)
    {
        _context = context;
    }

    public async Task ValidateBookingAsync(CreateBookingDto dto, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, List<string>>();

        void AddError(string propertyName, string message)
        {
            if (!errors.ContainsKey(propertyName))
            {
                errors[propertyName] = new List<string>();
            }
            errors[propertyName].Add(message);
        }

        // 1. Time Slot Validation
        if (dto.AppointmentStart >= dto.AppointmentEnd)
        {
            AddError(nameof(dto.AppointmentStart), "AppointmentStart must be earlier than AppointmentEnd.");
        }

        if (dto.AppointmentStart < DateTime.UtcNow.AddMinutes(-10))
        {
            AddError(nameof(dto.AppointmentStart), "AppointmentStart cannot be in the past.");
        }

        // 2. Carrier Validation
        if (dto.CarrierId == Guid.Empty)
        {
            AddError(nameof(dto.CarrierId), "CarrierId is required and cannot be empty.");
        }

        // 3. Driver Validation
        if (dto.DriverId.HasValue && dto.DriverId.Value != Guid.Empty)
        {
            var driver = await _context.Set<Driver.Domain.Entities.Driver>()
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == dto.DriverId.Value, cancellationToken);

            if (driver == null)
            {
                AddError(nameof(dto.DriverId), $"Driver with ID '{dto.DriverId}' was not found.");
            }
            else
            {
                if (driver.CarrierId != dto.CarrierId)
                {
                    AddError(nameof(dto.DriverId), $"Driver '{driver.FullName}' does not belong to the specified Transport Company (Carrier).");
                }

                if (!string.Equals(driver.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    AddError(nameof(dto.DriverId), $"Driver '{driver.FullName}' is currently not active (Status: {driver.Status}).");
                }
            }
        }

        // 4. Vehicle (Truck) Validation
        if (dto.TruckId.HasValue && dto.TruckId.Value != Guid.Empty)
        {
            var truck = await _context.Set<Vehicle.Domain.Entities.Vehicle>()
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == dto.TruckId.Value, cancellationToken);

            if (truck == null)
            {
                AddError(nameof(dto.TruckId), $"Vehicle with ID '{dto.TruckId}' was not found.");
            }
            else
            {
                if (truck.CarrierId != dto.CarrierId)
                {
                    AddError(nameof(dto.TruckId), $"Vehicle '{truck.PlateNumber}' does not belong to the specified Transport Company (Carrier).");
                }

                if (!string.Equals(truck.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    AddError(nameof(dto.TruckId), $"Vehicle '{truck.PlateNumber}' is currently not active (Status: {truck.Status}).");
                }
            }
        }

        // 5. Container Validation
        if (dto.ContainerIds != null && dto.ContainerIds.Any())
        {
            var distinctContainerIds = dto.ContainerIds.Distinct().ToList();
            var containers = await _context.Set<Container.Domain.Entities.Container>()
                .AsNoTracking()
                .Where(c => distinctContainerIds.Contains(c.Id))
                .ToListAsync(cancellationToken);

            if (containers.Count != distinctContainerIds.Count)
            {
                var foundIds = containers.Select(c => c.Id).ToHashSet();
                var missingIds = distinctContainerIds.Where(id => !foundIds.Contains(id));
                AddError(nameof(dto.ContainerIds), $"The following Container IDs were not found: {string.Join(", ", missingIds)}");
            }
            else
            {
                foreach (var container in containers)
                {
                    if (dto.BookingType == BookingType.Dropoff && string.Equals(container.Status, "gate_in", StringComparison.OrdinalIgnoreCase))
                    {
                        AddError(nameof(dto.ContainerIds), $"Container '{container.ContainerNumber}' is already inside the port (Status: {container.Status}).");
                    }
                    else if (dto.BookingType == BookingType.Pickup && string.Equals(container.Status, "gate_out", StringComparison.OrdinalIgnoreCase))
                    {
                        AddError(nameof(dto.ContainerIds), $"Container '{container.ContainerNumber}' has already exited the port (Status: {container.Status}).");
                    }
                    else if (string.Equals(container.Status, "canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        AddError(nameof(dto.ContainerIds), $"Container '{container.ContainerNumber}' is canceled and cannot be booked.");
                    }
                }
            }
        }

        // 6. Duplicate / Overlapping Booking Validation
        var activeStatuses = new[] { BookingStatus.Pending, BookingStatus.Approved, BookingStatus.CheckedIn };
        var overlappingBookings = await _context.Set<Domain.Entities.Booking>()
            .Include(b => b.BookingContainers)
            .AsNoTracking()
            .Where(b => activeStatuses.Contains(b.Status) &&
                        b.AppointmentStart < dto.AppointmentEnd &&
                        b.AppointmentEnd > dto.AppointmentStart)
            .ToListAsync(cancellationToken);

        if (overlappingBookings.Any())
        {
            if (dto.DriverId.HasValue && dto.DriverId.Value != Guid.Empty &&
                overlappingBookings.Any(b => b.DriverId == dto.DriverId.Value))
            {
                AddError(nameof(dto.DriverId), "Driver already has an active booking during this overlapping time slot.");
            }

            if (dto.TruckId.HasValue && dto.TruckId.Value != Guid.Empty &&
                overlappingBookings.Any(b => b.TruckId == dto.TruckId.Value))
            {
                AddError(nameof(dto.TruckId), "Vehicle (Truck) already has an active booking during this overlapping time slot.");
            }

            if (dto.ContainerIds != null && dto.ContainerIds.Any())
            {
                var reservedContainerIds = overlappingBookings
                    .SelectMany(b => b.BookingContainers)
                    .Select(bc => bc.ContainerId)
                    .ToHashSet();

                var duplicateContainers = dto.ContainerIds.Where(id => reservedContainerIds.Contains(id)).ToList();
                if (duplicateContainers.Any())
                {
                    AddError(nameof(dto.ContainerIds), $"Containers '{string.Join(", ", duplicateContainers)}' are already reserved in an active booking during this time slot.");
                }
            }
        }

        if (errors.Any())
        {
            var formattedErrors = errors.ToDictionary(k => k.Key, v => v.Value.ToArray());
            throw new ValidationException(formattedErrors);
        }
    }
}

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
            AddError(nameof(dto.AppointmentStart), "Khung giờ bắt đầu hẹn phải sớm hơn khung giờ kết thúc.");
        }

        if (dto.AppointmentStart < DateTime.UtcNow.AddMinutes(-10))
        {
            AddError(nameof(dto.AppointmentStart), "Khung giờ hẹn vào cổng không được ở trong quá khứ.");
        }

        // 2. Carrier Validation
        if (dto.CarrierId == Guid.Empty)
        {
            AddError(nameof(dto.CarrierId), "Thông tin Hãng vận tải (CarrierId) là bắt buộc và không được để trống.");
        }

        // 3. Driver Validation
        if (dto.DriverId.HasValue && dto.DriverId.Value != Guid.Empty)
        {
            var driver = await _context.Set<Driver.Domain.Entities.Driver>()
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == dto.DriverId.Value, cancellationToken);

            if (driver == null)
            {
                AddError(nameof(dto.DriverId), $"Không tìm thấy thông tin tài xế (Mã: '{dto.DriverId}') trong hệ thống cảng.");
            }
            else
            {
                if (driver.CarrierId != dto.CarrierId)
                {
                    AddError(nameof(dto.DriverId), $"Tài xế '{driver.FullName}' không thuộc quản lý của Hãng vận tải đã chọn.");
                }

                if (driver.Status == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.inactive || driver.Status == NexusPort.Modules.Driver.Domain.Enums.DriverStatus.banned)
                {
                    AddError(nameof(dto.DriverId), $"Tài xế '{driver.FullName}' hiện không ở trạng thái sẵn sàng (Trạng thái hiện tại: {driver.Status}).");
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
                AddError(nameof(dto.TruckId), $"Không tìm thấy thông tin xe đầu kéo (Mã: '{dto.TruckId}') trong hệ thống cảng.");
            }
            else
            {
                if (truck.CarrierId != dto.CarrierId)
                {
                    AddError(nameof(dto.TruckId), $"Xe đầu kéo '{truck.PlateNumber}' không thuộc đội xe của Hãng vận tải đã chọn.");
                }

                if (truck.Status == NexusPort.Modules.Vehicle.Domain.Enums.TruckStatus.inactive || truck.Status == NexusPort.Modules.Vehicle.Domain.Enums.TruckStatus.maintenance)
                {
                    AddError(nameof(dto.TruckId), $"Xe đầu kéo '{truck.PlateNumber}' hiện không ở trạng thái sẵn sàng (Trạng thái hiện tại: {truck.Status}).");
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
                AddError(nameof(dto.ContainerIds), $"Không tìm thấy các Container sau trong bãi cảng: {string.Join(", ", missingIds)}");
            }
            else
            {
                foreach (var container in containers)
                {
                    if (dto.BookingType == BookingType.Dropoff && string.Equals(container.Status, "gate_in", StringComparison.OrdinalIgnoreCase))
                    {
                        AddError(nameof(dto.ContainerIds), $"Container '{container.ContainerNumber}' đã nằm trong bãi cảng (Trạng thái: {container.Status}). Không thể tạo lịch hạ container.");
                    }
                    else if (dto.BookingType == BookingType.Pickup && string.Equals(container.Status, "gate_out", StringComparison.OrdinalIgnoreCase))
                    {
                        AddError(nameof(dto.ContainerIds), $"Container '{container.ContainerNumber}' đã rời khỏi cảng (Trạng thái: {container.Status}). Không thể tạo lịch lấy container.");
                    }
                    else if (string.Equals(container.Status, "canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        AddError(nameof(dto.ContainerIds), $"Container '{container.ContainerNumber}' đã bị hủy trên hệ thống và không thể đặt lịch.");
                    }
                }
            }
        }

        // 6. Duplicate / Overlapping Booking Validation
        var overlappingBookings = await _context.Set<Domain.Entities.Booking>()
            .Include(b => b.BookingContainers)
            .AsNoTracking()
            .Where(b => b.Status != BookingStatus.Canceled &&
                        b.Status != BookingStatus.Completed &&
                        b.Status != BookingStatus.Expired &&
                        b.Status != BookingStatus.Rejected &&
                        b.AppointmentStart < dto.AppointmentEnd &&
                        b.AppointmentEnd > dto.AppointmentStart)
            .ToListAsync(cancellationToken);

        if (overlappingBookings.Any())
        {
            if (dto.DriverId.HasValue && dto.DriverId.Value != Guid.Empty &&
                overlappingBookings.Any(b => b.DriverId == dto.DriverId.Value))
            {
                AddError(nameof(dto.DriverId), "Tài xế đã có một lịch hẹn đặt chỗ khác đang hoạt động trong khung giờ bị trùng lặp này.");
            }

            if (dto.TruckId.HasValue && dto.TruckId.Value != Guid.Empty &&
                overlappingBookings.Any(b => b.TruckId == dto.TruckId.Value))
            {
                AddError(nameof(dto.TruckId), "Xe đầu kéo đã có một lịch hẹn đặt chỗ khác đang hoạt động trong khung giờ bị trùng lặp này.");
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
                    AddError(nameof(dto.ContainerIds), $"Các Container sau đã được giữ chỗ trong một lịch hẹn khác trong khung giờ này: {string.Join(", ", duplicateContainers)}");
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

using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Booking.Domain.Entities;

public class Booking : BaseEntity, IAggregateRoot
{
    public Guid CarrierId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? TruckId { get; set; }

    public string BookingCode { get; set; } = string.Empty;
    public string BookingNumber { get => BookingCode; set => BookingCode = value; }
    public string? Description { get; set; }
    public BookingType BookingType { get; set; } = BookingType.Pickup;
    public BookingStatus Status { get; set; } = BookingStatus.Pending;

    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }

    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectedReason { get; set; }
    public DateTime? CanceledAt { get; set; }

    public ICollection<BookingContainer> BookingContainers { get; set; } = new List<BookingContainer>();

    // Associated Vehicle & Driver
    public string? VehiclePlate { get; set; }
    public Guid? VehicleId { get; set; }
    public string? DriverName { get; set; }

    // Time validity window
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }

    // Gate operation type (e.g. GateIn, GateOut, Delivery, Receiving)
    public string? GateType { get; set; } = "GateIn";

    public Booking() { }

    public Booking(
        Guid carrierId,
        string bookingCode,
        BookingType bookingType,
        DateTime appointmentStart,
        DateTime appointmentEnd,
        Guid? driverId = null,
        Guid? truckId = null)
    {
        CarrierId = carrierId;
        BookingCode = bookingCode;
        BookingType = bookingType;
        AppointmentStart = appointmentStart;
        AppointmentEnd = appointmentEnd;
        DriverId = driverId;
        TruckId = truckId;
        Status = BookingStatus.Pending;
    }

    public void AddContainer(Guid containerId, string? note = null)
    {
        if (!BookingContainers.Any(bc => bc.ContainerId == containerId))
        {
            BookingContainers.Add(new BookingContainer(Id, containerId, note));
        }
    }

    public void RemoveContainer(Guid containerId)
    {
        var item = BookingContainers.FirstOrDefault(bc => bc.ContainerId == containerId);
        if (item != null)
        {
            BookingContainers.Remove(item);
        }
    }

    public void Approve(Guid approvedBy)
    {
        Status = BookingStatus.Approved;
        ApprovedBy = approvedBy;
        ApprovedAt = DateTime.UtcNow;
    }

    public void Reject(string reason)
    {
        Status = BookingStatus.Rejected;
        RejectedReason = reason;
    }

    public void Cancel()
    {
        Status = BookingStatus.Canceled;
        CanceledAt = DateTime.UtcNow;
    }

    public void CheckIn()
    {
        Status = BookingStatus.CheckedIn;
    }

    public void Complete()
    {
        Status = BookingStatus.Completed;
    }
}

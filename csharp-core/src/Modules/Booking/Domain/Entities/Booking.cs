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
    public ICollection<BookingOperation> BookingOperations { get; set; } = new List<BookingOperation>();
    public ICollection<BookingDocument> BookingDocuments { get; set; } = new List<BookingDocument>();

    // Multi-operation and Trip tracking
    public bool IsCombined { get; set; } = false;
    public string TripStatus { get; set; } = "Scheduled"; // Scheduled, Arrived, Processing, PartiallyCompleted, Completed, Cancelled
    public Guid? CustomerId { get; set; }

    // Associated Vehicle & Driver
    public string? VehiclePlate { get; set; }
    public Guid? VehicleId { get; set; }
    public string? DriverName { get; set; }
    public string? VehicleSnapshot { get; set; }
    public string? DriverSnapshot { get; set; }
    public string? TrailerNumber { get; set; }

    // Time validity window
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }

    // Gate operation type (e.g. GateIn, GateOut, Delivery, Receiving)
    public string? GateType { get; set; } = "GateIn";

    // Cold Storage Reefer Power Supply properties
    public bool IsReeferPowerRequired { get; set; }
    public decimal? TargetTemperature { get; set; }
    public string? TemperatureUnit { get; set; } = "C";
    public decimal? TempTolerance { get; set; } = 2.0m;
    public Guid? ReeferPlugId { get; set; }
    public string? ReeferPlugCode { get; set; }
    public string? ReeferPowerStatus { get; set; }
    public decimal? RequiredPowerKw { get; set; } = 15.0m;

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

    public void AssignFleet(Guid driverId, string? driverName, Guid truckId, string? vehiclePlate, Guid? containerId = null)
    {
        DriverId = driverId;
        if (!string.IsNullOrWhiteSpace(driverName)) DriverName = driverName;
        TruckId = truckId;
        VehicleId = truckId;
        if (!string.IsNullOrWhiteSpace(vehiclePlate)) VehiclePlate = vehiclePlate;
        if (containerId.HasValue && containerId.Value != Guid.Empty)
        {
            AddContainer(containerId.Value);
        }
        Status = BookingStatus.Ready;
    }

    public void MarkReady()
    {
        Status = BookingStatus.Ready;
    }

    public void AddOperation(BookingOperation operation)
    {
        operation.BookingId = Id;
        BookingOperations.Add(operation);
        if (BookingOperations.Count > 1)
        {
            IsCombined = true;
        }
    }

    public void AddDocument(BookingDocument document)
    {
        document.BookingId = Id;
        BookingDocuments.Add(document);
    }

    public void UpdateDerivedStatus()
    {
        if (!BookingOperations.Any()) return;

        var allCompleted = BookingOperations.All(o => o.Status == "Completed");
        var anyInProgress = BookingOperations.Any(o => o.Status == "InProgress");
        var anyCompleted = BookingOperations.Any(o => o.Status == "Completed");
        var allApproved = BookingOperations.All(o => o.Status == "Approved");
        var allCancelled = BookingOperations.All(o => o.Status == "Cancelled");

        if (allCompleted)
        {
            Status = BookingStatus.Completed;
            TripStatus = "Completed";
        }
        else if (anyInProgress)
        {
            Status = BookingStatus.CheckedIn;
            TripStatus = "Processing";
        }
        else if (anyCompleted)
        {
            TripStatus = "PartiallyCompleted";
        }
        else if (allApproved)
        {
            Status = BookingStatus.Approved;
            TripStatus = "Scheduled";
        }
        else if (allCancelled)
        {
            Status = BookingStatus.Canceled;
            TripStatus = "Cancelled";
        }
    }
}

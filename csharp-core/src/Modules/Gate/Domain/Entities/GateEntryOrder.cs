namespace NexusPort.Modules.Gate.Domain.Entities;

public class GateEntryOrder
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public Guid DriverId { get; set; }
    public Guid? TruckId { get; set; }
    public string VehiclePlate { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
    public DateTime IssuedAt { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
}

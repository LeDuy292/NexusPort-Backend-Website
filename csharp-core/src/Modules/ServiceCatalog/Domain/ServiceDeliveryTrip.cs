namespace NexusPort.Modules.ServiceCatalog.Domain;

public sealed record ServiceDeliveryTrip
{
    public Guid SourceContainerId { get; init; }
    public string? AppointmentStart { get; init; }
    public string? AppointmentEnd { get; init; }
    public Guid? TruckId { get; init; }
    public Guid? DriverId { get; init; }
    // Server-owned text snapshots. Expiring document URLs are never stored in the draft/token.
    public ServiceTripVehicle? Vehicle { get; init; }
    public ServiceTripDriver? Driver { get; init; }
}

public sealed record ServiceTripVehicle(Guid Id, string PlateNumber, string VehicleType, string Status,
    string? CurrentLocation, string? RfidTag, string? Description);
public sealed record ServiceTripDriver(Guid Id, string FullName, string? Phone, string LicenseNumber,
    string Status, DateTime? LicenseExpiryDate, string? IdCardNumber, DateTime? IdCardExpiryDate);

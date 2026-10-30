namespace NexusPort.Modules.Booking.Application.DTOs;

public class BookingVehicleDetailsDto
{
    public Guid Id { get; set; }
    public Guid CarrierId { get; set; }
    public Guid? DriverId { get; set; }
    public string PlateNumber { get; set; } = "";
    public string VehicleType { get; set; } = "";
    public string Status { get; set; } = "";
    public string? RfidTag { get; set; }
    public string? Description { get; set; }
    public string? CurrentLocation { get; set; }
    public string? PhotoUrl { get; set; }
    public string? RegistrationImageUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CapturedAt { get; set; }
}

public class BookingDriverDetailsDto
{
    public Guid Id { get; set; }
    public Guid CarrierId { get; set; }
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string? IdCardNumber { get; set; }
    public string LicenseNumber { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? IdCardExpiryDate { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
    public string? PhotoUrl { get; set; }
    public string? IdCardFrontUrl { get; set; }
    public string? IdCardBackUrl { get; set; }
    public string? LicenseImageUrl { get; set; }
    public string? LicenseBackImageUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CapturedAt { get; set; }
}

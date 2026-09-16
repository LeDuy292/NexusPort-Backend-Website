using NexusPort.Shared.Kernel;
using NexusPort.Modules.Driver.Domain.Enums;

namespace NexusPort.Modules.Driver.Domain.Entities;

public class Driver : BaseEntity, IAggregateRoot
{
    public Guid CarrierId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? IdCardNumber { get; set; }
    public string LicenseNumber { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string? IdCardFrontUrl { get; set; }
    public string? LicenseImageUrl { get; set; }
    public DriverStatus Status { get; set; } = DriverStatus.active;

    public Driver() { }

    public Driver(Guid carrierId, string fullName, string licenseNumber, string? phone = null, string? idCardNumber = null, DriverStatus status = DriverStatus.active)
    {
        CarrierId = carrierId;
        FullName = fullName;
        LicenseNumber = licenseNumber;
        Phone = phone;
        IdCardNumber = idCardNumber;
        Status = status;
    }
}

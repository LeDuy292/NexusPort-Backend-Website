using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Driver.Domain.Entities;

public class Driver : BaseEntity, IAggregateRoot
{
    public Guid CarrierId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? IdCardNumber { get; set; }
    public string LicenseNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "active";

    public Driver() { }

    public Driver(Guid carrierId, string fullName, string licenseNumber, string? phone = null, string? idCardNumber = null, string status = "active")
    {
        CarrierId = carrierId;
        FullName = fullName;
        LicenseNumber = licenseNumber;
        Phone = phone;
        IdCardNumber = idCardNumber;
        Status = status;
    }
}

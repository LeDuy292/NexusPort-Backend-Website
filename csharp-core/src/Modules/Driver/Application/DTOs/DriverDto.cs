namespace NexusPort.Modules.Driver.Application.DTOs;

public class DriverDto
{
    public Guid Id { get; set; }
    public Guid CarrierId { get; set; }
    public string? CarrierName { get; set; }
    public string FullName { get; set; } = null!;
    public string Phone { get; set; } = null!;
    public string IdCardNumber { get; set; } = null!;
    public string LicenseNumber { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? PhotoUrl { get; set; }
    public string? IdCardFrontUrl { get; set; }
    public string? IdCardBackUrl { get; set; }
    public string? LicenseImageUrl { get; set; }
    public string? LicenseBackImageUrl { get; set; }
    public DateTime? IdCardExpiryDate { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateDriverDto
{
    public string FullName { get; set; } = null!;
    public string Phone { get; set; } = null!;
    public string IdCardNumber { get; set; } = null!;
    public string LicenseNumber { get; set; } = null!;
    public string? PhotoUrl { get; set; }
    public string? IdCardFrontUrl { get; set; }
    public string? IdCardBackUrl { get; set; }
    public string? LicenseImageUrl { get; set; }
    public string? LicenseBackImageUrl { get; set; }
    public DateTime? IdCardExpiryDate { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
}

public class UpdateDriverDto
{
    public string FullName { get; set; } = null!;
    public string Phone { get; set; } = null!;
    public string IdCardNumber { get; set; } = null!;
    public string? PhotoUrl { get; set; }
    public string? IdCardFrontUrl { get; set; }
    public string? IdCardBackUrl { get; set; }
    public string? LicenseImageUrl { get; set; }
    public string? LicenseBackImageUrl { get; set; }
    public DateTime? IdCardExpiryDate { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
}

public class DriverFilterDto
{
    public Guid? CarrierId { get; set; }
    public string? Status { get; set; }
    public string? SearchTerm { get; set; }
}

namespace NexusPort.Modules.Driver.Application.DTOs;

public class DriverDto
{
    public Guid? UserId { get; set; }
    public bool? AccountActive { get; set; }
    public string? ActiveBookingCode { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public bool? InvitationPending { get; set; }
    public bool EmailVerified { get; set; }
    public DateTime? EmailVerifiedAt { get; set; }
    public DateTime? InvitationExpiresAt { get; set; }
    public Guid Id { get; set; }
    public Guid CarrierId { get; set; }
    public string? CarrierName { get; set; }
    public string FullName { get; set; } = null!;
    public string Phone { get; set; } = null!;
    public string IdCardNumber { get; set; } = null!;
    public string LicenseNumber { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? PhotoUrl { get; set; }
    public bool HasPhoto { get; set; }
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
    public bool IncludePhotos { get; set; } = true;
    public bool IncludeDocuments { get; set; } = true;
    public Guid? CarrierId { get; set; }
    public string? Status { get; set; }
    public string? SearchTerm { get; set; }
}

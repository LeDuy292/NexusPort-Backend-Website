using System.ComponentModel.DataAnnotations;

namespace NexusPort.Modules.Identity.Application.DTOs;

public sealed class DriverAccountDto
{
    [Required, RegularExpression(@"^[a-zA-Z0-9_.-]{3,100}$")]
    public string Username { get; set; } = "";
    [Required, EmailAddress, MaxLength(254)] public string Email { get; set; } = "";
    public Guid? DriverId { get; set; }
    [Required, MinLength(2), MaxLength(150)] public string FullName { get; set; } = "";
    [Required, RegularExpression(@"^\+?[0-9]{9,15}$")] public string Phone { get; set; } = "";
    [Required, RegularExpression(@"^[0-9]{12}$")] public string IdCardNumber { get; set; } = "";
    [Required, MaxLength(80)] public string LicenseNumber { get; set; } = "";
    [MaxLength(500)] public string? PhotoUrl { get; set; }
    [MaxLength(500)] public string? IdCardFrontUrl { get; set; }
    [MaxLength(500)] public string? IdCardBackUrl { get; set; }
    [MaxLength(500)] public string? LicenseImageUrl { get; set; }
    [MaxLength(500)] public string? LicenseBackImageUrl { get; set; }
    public DateTime? IdCardExpiryDate { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
}

public sealed class ActivateDriverAccountDto
{
    [Required, RegularExpression("^[a-f0-9]{64}$")] public string Token { get; set; } = "";
    [Required, MinLength(10), MaxLength(72)] public string Password { get; set; } = "";
}

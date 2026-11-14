namespace NexusPort.Modules.Driver.Application.DTOs;

public sealed class DriverAccountLink
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public bool? AccountActive { get; set; }
    public string? ActiveBookingCode { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public bool? InvitationPending { get; set; }
    public DateTime? EmailVerifiedAt { get; set; }
    public DateTime? InvitationExpiresAt { get; set; }
}

namespace NexusPort.Modules.Vessel.Application.DTOs;

public class VesselDto
{
    public Guid Id { get; set; }
    public string VesselName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateVesselDto
{
    public string VesselName { get; set; } = string.Empty;
    public string? Description { get; set; }
}

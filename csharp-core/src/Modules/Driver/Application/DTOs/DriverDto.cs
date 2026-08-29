namespace NexusPort.Modules.Driver.Application.DTOs;

public class DriverDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateDriverDto
{
    public string FullName { get; set; } = string.Empty;
    public string? Description { get; set; }
}

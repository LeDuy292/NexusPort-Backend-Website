namespace NexusPort.Modules.Container.Application.DTOs;

public class ContainerDto
{
    public Guid Id { get; set; }
    public string ContainerNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateContainerDto
{
    public string ContainerNumber { get; set; } = string.Empty;
    public string? Description { get; set; }
}

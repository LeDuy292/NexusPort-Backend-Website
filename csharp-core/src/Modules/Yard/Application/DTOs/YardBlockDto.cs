namespace NexusPort.Modules.Yard.Application.DTOs;

public class YardBlockDto
{
    public Guid Id { get; set; }
    public string BlockCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateYardBlockDto
{
    public string BlockCode { get; set; } = string.Empty;
    public string? Description { get; set; }
}

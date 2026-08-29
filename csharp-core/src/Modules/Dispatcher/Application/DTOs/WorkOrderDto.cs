namespace NexusPort.Modules.Dispatcher.Application.DTOs;

public class WorkOrderDto
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateWorkOrderDto
{
    public string OrderNumber { get; set; } = string.Empty;
    public string? Description { get; set; }
}

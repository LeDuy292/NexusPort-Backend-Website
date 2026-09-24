namespace NexusPort.Modules.Yard.Application.DTOs;

public class YardBlockDto
{
    public Guid Id { get; set; }
    public string BlockCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int MaxBays { get; set; }
    public int MaxRows { get; set; }
    public int MaxTiers { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<YardSlotDto> Slots { get; set; } = new();
}

public class YardSlotDto
{
    public Guid Id { get; set; }
    public Guid YardBlockId { get; set; }
    public int Bay { get; set; }
    public int Row { get; set; }
    public int Tier { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? ContainerId { get; set; }
    public string? ContainerNumber { get; set; }
}

public class CreateYardBlockDto
{
    public string BlockCode { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class CompleteYardOperationDto
{
    public Guid ContainerId { get; set; }
    public Guid DriverId { get; set; }
    public string OperationStatus { get; set; } = "Completed";
}

public class YardOperationCompletionDto
{
    public Guid EventId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ContainerId { get; set; }
    public Guid DriverId { get; set; }
    public string OperationStatus { get; set; } = string.Empty;
    public string DeliveryStatus { get; set; } = string.Empty;
    public DateTime CompletedAt { get; set; }
}

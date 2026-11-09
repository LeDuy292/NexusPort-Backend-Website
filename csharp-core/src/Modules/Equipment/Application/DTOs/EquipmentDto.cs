namespace NexusPort.Modules.Equipment.Application.DTOs;

public class EquipmentDto
{
    public Guid Id { get; set; }
    public string EquipmentCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EquipmentType { get; set; } = "RTG";
    public string Status { get; set; } = "available";
    public string BlockCode { get; set; } = "A01";
    public Guid? OperatorId { get; set; }
    public string? OperatorName { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateEquipmentDto
{
    public string EquipmentCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EquipmentType { get; set; } = "RTG";
    public string Status { get; set; } = "available";
    public string BlockCode { get; set; } = "A01";
    public string? Description { get; set; }
}

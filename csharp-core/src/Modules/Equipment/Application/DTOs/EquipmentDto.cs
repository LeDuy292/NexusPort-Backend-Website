namespace NexusPort.Modules.Equipment.Application.DTOs;

public class EquipmentDto
{
    public Guid Id { get; set; }
    public string EquipmentCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateEquipmentDto
{
    public string EquipmentCode { get; set; } = string.Empty;
    public string? Description { get; set; }
}

namespace NexusPort.Modules.Gate.Application.DTOs;

public class GateTransactionDto
{
    public Guid Id { get; set; }
    public string TransactionCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateGateTransactionDto
{
    public string TransactionCode { get; set; } = string.Empty;
    public string? Description { get; set; }
}

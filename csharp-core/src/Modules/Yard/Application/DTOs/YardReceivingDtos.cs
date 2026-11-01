namespace NexusPort.Modules.Yard.Application.DTOs;

public class VerifyYardContainerDto
{
    public string ContainerNo { get; set; } = string.Empty;
    public string ActualSealNo { get; set; } = string.Empty;
}

public class YardContainerVerificationResultDto
{
    public Guid ContainerId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public string ExpectedSealNo { get; set; } = string.Empty;
    public string ActualSealNo { get; set; } = string.Empty;
    public bool IsMatchingContainer { get; set; }
    public bool IsMatchingSeal { get; set; }
    public string CurrentStatus { get; set; } = string.Empty;
    public string IsoType { get; set; } = string.Empty;
    public string Weight { get; set; } = string.Empty;
    public string AssignedBlock { get; set; } = string.Empty;
    public string AssignedLocation { get; set; } = string.Empty;
}

public class CreateYardReceiptDto
{
    public string ContainerNo { get; set; } = string.Empty;
    public string ActualSealNo { get; set; } = string.Empty;
    public bool IsSealIntact { get; set; } = true;
    public string Condition { get; set; } = "Good"; // Good, Damaged, SealBroken
    public string? Notes { get; set; }
    public string YardBlockCode { get; set; } = "A01";
    public string LocationCoordinate { get; set; } = "A01-05-02-3";
    public string InspectorName { get; set; } = "Nguyễn Văn Bãi";
}

public class YardReceiptDto
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public string ExpectedSealNo { get; set; } = string.Empty;
    public string ActualSealNo { get; set; } = string.Empty;
    public bool IsSealIntact { get; set; }
    public bool IsMatchingContainer { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string ReceivedBy { get; set; } = string.Empty;
    public string InspectorName { get; set; } = string.Empty;
    public string YardBlockCode { get; set; } = string.Empty;
    public string LocationCoordinate { get; set; } = string.Empty;
}

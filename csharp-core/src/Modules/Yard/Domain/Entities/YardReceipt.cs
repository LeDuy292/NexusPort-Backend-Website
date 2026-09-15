using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Domain.Entities;

public class YardReceipt : BaseEntity, IAggregateRoot
{
    public Guid ContainerId { get; set; }
    public string ContainerNo { get; set; } = string.Empty;
    public string ExpectedSealNo { get; set; } = string.Empty;
    public string ActualSealNo { get; set; } = string.Empty;
    public bool IsSealIntact { get; set; } = true;
    public bool IsMatchingContainer { get; set; } = true;
    public string Condition { get; set; } = "Good"; // Good, Damaged, SealBroken
    public string? Notes { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string ReceivedBy { get; set; } = string.Empty;
    public string InspectorName { get; set; } = string.Empty;
    public string YardBlockCode { get; set; } = string.Empty;
    public string LocationCoordinate { get; set; } = string.Empty; // Block-Bay-Row-Tier

    public YardReceipt() { }

    public YardReceipt(
        Guid containerId,
        string containerNo,
        string expectedSealNo,
        string actualSealNo,
        bool isSealIntact,
        bool isMatchingContainer,
        string condition,
        string? notes,
        string receivedBy,
        string inspectorName,
        string yardBlockCode,
        string locationCoordinate)
    {
        ContainerId = containerId;
        ContainerNo = containerNo;
        ExpectedSealNo = expectedSealNo;
        ActualSealNo = actualSealNo;
        IsSealIntact = isSealIntact;
        IsMatchingContainer = isMatchingContainer;
        Condition = condition;
        Notes = notes;
        ReceivedAt = DateTime.UtcNow;
        ReceivedBy = receivedBy;
        InspectorName = inspectorName;
        YardBlockCode = yardBlockCode;
        LocationCoordinate = locationCoordinate;
    }
}

using NexusPort.Shared.Kernel;

namespace NexusPort.Modules.Yard.Preparation.Domain;

public class EDoRecord : BaseEntity
{
    public string ContainerNo { get; set; } = string.Empty;
    public ReleaseStatus ReleaseStatus { get; set; } = ReleaseStatus.Valid;
    public HoldFlag HoldFlag { get; set; } = HoldFlag.None;
    public DateTimeOffset ExpiresAt { get; set; }
    public string SealNo { get; set; } = string.Empty;
    public DateTimeOffset? FreeTimeUntil { get; set; }

    public EDoRecord() { }

    public EDoRecord(string containerNo, string sealNo, DateTimeOffset expiresAt, ReleaseStatus releaseStatus = ReleaseStatus.Valid, HoldFlag holdFlag = HoldFlag.None, DateTimeOffset? freeTimeUntil = null)
    {
        Id = Guid.NewGuid();
        ContainerNo = containerNo;
        SealNo = sealNo;
        ExpiresAt = expiresAt;
        ReleaseStatus = releaseStatus;
        HoldFlag = holdFlag;
        FreeTimeUntil = freeTimeUntil;
        CreatedAt = DateTime.UtcNow;
    }

    public bool IsReleasable()
    {
        return ReleaseStatus == ReleaseStatus.Valid &&
               HoldFlag == HoldFlag.None &&
               ExpiresAt > DateTimeOffset.UtcNow;
    }
}

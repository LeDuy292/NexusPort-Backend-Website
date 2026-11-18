namespace NexusPort.Modules.Yard.Preparation.Domain;

public enum PreparationDirection
{
    Inbound,
    Outbound
}

public enum OutboundMarshallingMode
{
    InBayHousekeeping,
    ApronBufferStaging
}

public enum PreparationTaskStatus
{
    Pending,
    Planned,
    InProgress,
    Ready,
    Delayed,
    Cancelled,
    Failed
}

public enum WorkOrderType
{
    Rehandle,   // Đảo chuyển container chắn
    Stage,      // Chuẩn bị giao ra bãi đệm (Chiều nhập)
    LoadApron   // Chuẩn bị xuất ra chân cẩu xếp tàu (Chiều xuất)
}

public enum WorkOrderStatus
{
    Pending,
    Assigned,
    InProgress,
    Done,
    Cancelled,
    Failed
}

public enum ChargeParty
{
    Port,       // Cảng chịu phí (quy hoạch bãi bình thường)
    Customer,   // Chủ hàng chịu phí (đổi/hủy lịch muộn)
    Carrier     // Hãng tàu chịu phí (cắt rớt tàu / đổi chuyến)
}

public enum ReleaseStatus
{
    Valid,
    Expired,
    Cancelled
}

public enum HoldFlag
{
    None,
    CustomsHold,
    LineHold,
    PortHold
}

public enum ExceptionSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum ExceptionStatus
{
    Open,
    Resolved
}

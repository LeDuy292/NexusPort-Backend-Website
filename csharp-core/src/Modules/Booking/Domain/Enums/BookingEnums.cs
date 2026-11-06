using System.Text.Json.Serialization;

namespace NexusPort.Modules.Booking.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookingOperationType
{
    Export,
    Import,
    EmptyPickup,
    EmptyReturn
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookingOperationStatus
{
    PendingVerification,
    Approved,
    OnHold,
    InProgress,
    Completed,
    Rejected,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookingDocumentType
{
    CarrierBookingReference,
    BillOfLading,
    DeliveryOrder,
    CustomsDeclaration,
    EmptyContainerReleaseOrder,
    EmptyReturnAuthorization,
    DangerousGoodsDeclaration,
    ReeferTemperatureInstructions,
    VgmCertificate,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookingDocumentStatus
{
    Missing,
    Uploaded,
    UnderVerification,
    Verified,
    Rejected,
    Expired
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GateDirection
{
    GateIn,
    GateOut
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CargoHandlingStatus
{
    Loaded,
    Empty
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TripVisitStatus
{
    Scheduled,
    Arrived,
    Processing,
    PartiallyCompleted,
    Completed,
    Cancelled
}

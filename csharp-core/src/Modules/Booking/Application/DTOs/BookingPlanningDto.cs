namespace NexusPort.Modules.Booking.Application.DTOs;

public sealed class BookingPlanningRequest : BookingPlanningLeg
{
    public Guid? BookingId { get; set; }
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public List<BookingPlanningLeg> Legs { get; set; } = new();
}

public class BookingPlanningLeg
{
    public Guid ContainerId { get; set; }
    public Guid? DeclarationId { get; set; }
    public string? YardCode { get; set; }
    public string BookingType { get; set; } = "Dropoff";
}

public sealed class BookingCargoSource
{
    public string? YardCode { get; set; }
    public Guid ContainerId { get; set; }
    public Guid DeclarationId { get; set; }
    public Guid? ContainerVisitId { get; set; }
    public string ContainerNumber { get; set; } = "";
    public string ContainerTypeCode { get; set; } = "";
    public string LoadStatus { get; set; } = "";
    public string? CargoType { get; set; }
    public string? SealNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? CarrierName { get; set; }
    public decimal? GrossWeightKg { get; set; }
    public decimal? RequiredPowerKw { get; set; }
}

public sealed class BookingFleetOptions : AvailableFleetResourcesDto
{
    public Guid? RecommendedTruckId { get; set; }
    public Guid? RecommendedDriverId { get; set; }
    public string Message { get; set; } = "";
    public int CompanyTruckCount { get; set; }
    public int CompanyDriverCount { get; set; }
    public List<string> UnavailableReasons { get; set; } = new();
}

public sealed class BookingPlan
{
    public BookingCargoSource Source { get; set; } = new();
    public List<BookingCargoSource> Sources { get; set; } = new();
    public BookingFleetOptions Fleet { get; set; } = new();
    public BookingScheduleAssessment Schedule { get; set; } = new();
}

public sealed class BookingScheduleAssessment
{
    public bool IsAvailable { get; set; }
    public bool RequiresExistingBookingResolution { get; set; }
    public string Message { get; set; } = "";
    public Guid? ExistingBookingId { get; set; }
    public string? ExistingBookingCode { get; set; }
    public List<BookingTimeOption> Alternatives { get; set; } = new();
}

public sealed class BookingTimeOption
{
    public DateTime AppointmentStart { get; set; }
    public DateTime AppointmentEnd { get; set; }
    public string TruckPlate { get; set; } = "";
    public string DriverName { get; set; } = "";
}

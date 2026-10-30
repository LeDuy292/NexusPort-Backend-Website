using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Shared.Results;

namespace NexusPort.Modules.Booking.Application.Interfaces;

public interface IBookingRepository
{
    Task<Domain.Entities.Booking?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Domain.Entities.Booking?> GetByIdWithContainersAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Domain.Entities.Booking>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<PagedResult<Domain.Entities.Booking>> GetPagedAsync(BookingFilterParams filter, CancellationToken cancellationToken = default);
    Task AddAsync(Domain.Entities.Booking entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(Domain.Entities.Booking entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IBookingService
{
    Task<IReadOnlyList<BookingCargoSource>> GetBookingSourcesAsync(Guid carrierId, string? search, CancellationToken token, string bookingType = "Dropoff");
    Task<BookingPlan> PlanBookingAsync(Guid carrierId, BookingPlanningRequest request, CancellationToken token);
    Task<IReadOnlyList<BookingDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<PagedResult<BookingDto>> GetPagedAsync(BookingFilterParams filter, CancellationToken cancellationToken = default);
    Task<BookingDto?> GetByIdAsync(Guid id, Guid? userCarrierId = null, CancellationToken cancellationToken = default);
    Task<BookingDto> CreateAsync(CreateBookingDto dto, CancellationToken cancellationToken = default);
    Task<BookingDto> UpdateAsync(Guid id, UpdateBookingDto dto, Guid? userCarrierId = null, CancellationToken cancellationToken = default);
    Task<BookingDto> CancelAsync(Guid id, CancelBookingDto dto, Guid? userCarrierId = null, CancellationToken cancellationToken = default);
    Task<BookingDto> ApproveAsync(Guid id, Guid approvedBy, CancellationToken cancellationToken = default);
    Task<BookingDto> RejectAsync(Guid id, string reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DriverContainerOperationDto>> GetDriverOperationsAsync(Guid driverId, CancellationToken cancellationToken = default);
    Task<ContainerConfirmationResultDto> ConfirmContainerAsync(ContainerConfirmationDto dto, Guid driverId, CancellationToken cancellationToken = default);
    Task<BookingDto> AssignResourcesAsync(Guid id, AssignBookingResourcesDto dto, Guid? userCarrierId = null, CancellationToken cancellationToken = default);
    Task<AvailableFleetResourcesDto> GetAvailableResourcesAsync(Guid? carrierId = null, CancellationToken cancellationToken = default);
    Task<FleetRecommendationDto> RecommendFleetAsync(Guid? containerId = null, string? bookingType = null, Guid? carrierId = null, CancellationToken cancellationToken = default, Guid? declarationId = null, string? yardCode = null, Guid? bookingId = null);
    Task<PayloadEvaluationDto> EvaluatePayloadAsync(Guid? containerId = null, Guid? truckId = null, decimal? customGrossWeightTon = null, CancellationToken cancellationToken = default);
    Task<BookingPaymentInfoDto> GetPaymentInfoAsync(Guid id, CancellationToken cancellationToken = default);
    Task<BookingPaymentInfoDto> ProcessPaymentAsync(Guid id, ProcessBookingPaymentDto dto, CancellationToken cancellationToken = default);

    // Multi-operation and Conditional Document management
    Task<IReadOnlyList<BookingOperationDto>> GetOperationsAsync(Guid bookingId, CancellationToken cancellationToken = default);
    Task<BookingOperationDto> UpdateOperationStatusAsync(Guid bookingId, Guid operationId, UpdateBookingOperationStatusDto dto, Guid? updatedBy = null, CancellationToken cancellationToken = default);
    Task<BookingDocumentDto> AddDocumentAsync(Guid bookingId, CreateBookingDocumentDto dto, CancellationToken cancellationToken = default);
    Task<BookingDocumentDto> VerifyDocumentAsync(Guid bookingId, Guid documentId, VerifyBookingDocumentDto dto, Guid verifiedBy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NexusPort.Modules.Booking.Application.Services.DocumentRequirementSpec>> GetDocumentRequirementsAsync(string operationType, string cargoStatus, string cargoType, CancellationToken cancellationToken = default);
    Task<NexusPort.Modules.Booking.Application.Services.OperationCombinationValidationResult> ValidateCombinationAsync(IReadOnlyList<CreateBookingOperationDto> operations, CancellationToken cancellationToken = default);
}

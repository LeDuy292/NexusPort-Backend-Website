using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Services;

namespace NexusPort.Modules.Booking.Presentation.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Admin,Transport Company,Carrier,Carrier Staff,Shipping Line,Dispatcher,Gate Officer,Gate,Yard Operator,Berth Staff")]
[Route("api/v1/Booking/export")]
public sealed class ExportBookingController : ControllerBase
{
    private const string Customers = "Transport Company,Carrier,Carrier Staff";
    private const string Lines = "Shipping Line,Administrator,Admin";
    private const string PortStaff = "Administrator,Admin,Dispatcher,Gate Officer,Gate,Yard Operator,Berth Staff";
    private readonly ExportBookingService _service;
    public ExportBookingController(ExportBookingService service) => _service = service;

    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct) => Ok(await _service.Options(ct));
    [Authorize(Roles = Customers)] [HttpGet("drafts")]
    public async Task<IActionResult> Drafts(CancellationToken ct) => Ok(await _service.Drafts(ct));
    [Authorize(Roles = Customers)] [HttpGet("drafts/{id:guid}")]
    public async Task<IActionResult> GetDraft(Guid id, CancellationToken ct) => Ok(await _service.GetDraft(id, ct));
    [Authorize(Roles = Customers)] [HttpPut("drafts/{id:guid}")]
    public async Task<IActionResult> SaveDraft(Guid id, ExportDraftSaveRequest request, CancellationToken ct) => Ok(await _service.SaveDraft(id, request, ct));
    [Authorize(Roles = Customers)] [HttpPost("preparation/readiness")]
    public async Task<IActionResult> Readiness(ExportPreparationContent content, CancellationToken ct) => Ok(await _service.PreparationReadiness(content, ct));
    [Authorize(Roles = Customers)] [HttpPost("requests")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("booking-create")]
    public async Task<IActionResult> SubmitPreparation(ExportPreparationRequest request, CancellationToken ct) => Ok(await _service.SubmitPreparation(request, ct));
    [Authorize(Roles = Customers)] [HttpPost("{id:guid}/preparation/confirm")]
    public async Task<IActionResult> ConfirmPreparation(Guid id, ExportPreparationRequest request, CancellationToken ct) => Ok(await _service.ConfirmPreparation(id, request, ct));
    [Authorize(Roles = Lines)] [HttpPost("{id:guid}/container-review")]
    public async Task<IActionResult> ReviewContainers(Guid id, ExportContainerReviewRequest request, CancellationToken ct) => Ok(await _service.ReviewPreparedContainers(id, request, ct));
    [Authorize(Roles = Customers)] [HttpPost("documents")] [RequestSizeLimit(27 * 1024 * 1024)]
    public async Task<IActionResult> UploadDocument(IFormFile file, CancellationToken ct) => Ok(await _service.UploadPreparationDocument(file, ct));
    [Authorize(Roles = Customers)] [HttpGet("{id:guid}/si-suggestions")]
    public async Task<IActionResult> SiSuggestions(Guid id, CancellationToken ct) => Ok(await _service.SiSuggestions(id, ct));
    [Authorize(Roles = Customers)] [HttpPost("submissions/plan")]
    public async Task<IActionResult> PlanSubmission(ExportSubmissionRequest request, CancellationToken ct) => Ok(await _service.PlanSubmission(request, ct));
    [Authorize(Roles = Customers)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("booking-create")]
    [HttpPost("submissions")]
    public async Task<IActionResult> Submit(ExportSubmissionRequest request, CancellationToken ct)
    {
        var result = await _service.Submit(request, ct);
        return CreatedAtAction(nameof(Detail), new { id = result["id"]!.GetValue<string>() }, result);
    }
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null, [FromQuery] string? status = null,
        [FromQuery] Guid? voyageId = null, CancellationToken ct = default)
        => Ok(await _service.List(page, pageSize, search, status, voyageId, ct));
    [Authorize(Roles = Customers)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("booking-create")]
    [HttpPost]
    public async Task<IActionResult> Create(CreateExportBookingRequest request, CancellationToken ct)
    {
        var result = await _service.Create(request, ct);
        return CreatedAtAction(nameof(Detail), new { id = result["id"]!.GetValue<string>() }, result);
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct) => Ok(await _service.Detail(id, ct));
    [Authorize(Roles = Lines)] [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.Decision(id, request, true, ct));
    [Authorize(Roles = Lines)] [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.Decision(id, request, false, ct));
    [Authorize(Roles = Customers)] [HttpPost("{id:guid}/containers")]
    public async Task<IActionResult> Containers(Guid id, DeclareExportContainersRequest request, CancellationToken ct) => Ok(await _service.Declare(id, request, ct));
    [Authorize(Roles = Customers)] [HttpPost("{id:guid}/si")]
    public async Task<IActionResult> Si(Guid id, ExportSiRequest request, CancellationToken ct) => Ok(await _service.SubmitSi(id, request, ct));
    [Authorize(Roles = Lines)] [HttpPost("{id:guid}/draft-bl")]
    public async Task<IActionResult> Draft(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.Draft(id, request, false, ct));
    [Authorize(Roles = Lines)] [HttpPost("{id:guid}/request-si-revision")]
    public async Task<IActionResult> Revise(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.Draft(id, request, true, ct));
    [Authorize(Roles = Customers)] [HttpPost("{id:guid}/confirm-draft")]
    public async Task<IActionResult> Confirm(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.ConfirmDraft(id, request, ct));
    [Authorize(Roles = Customers)] [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.Cancel(id, request, ct));
    [Authorize(Roles = Lines)] [HttpPost("{id:guid}/freight-payment")]
    public async Task<IActionResult> Freight(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.Freight(id, request, ct));
    [Authorize(Roles = Lines)] [HttpPost("{id:guid}/issue-bl")]
    public async Task<IActionResult> Bl(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.IssueBl(id, request, ct));
    [Authorize(Roles = PortStaff)] [HttpPost("{id:guid}/port-event")]
    public async Task<IActionResult> Port(Guid id, ExportPortEventRequest request, CancellationToken ct) => Ok(await _service.PortEvent(id, request, ct));
    [HttpGet("voyages")]
    public async Task<IActionResult> Voyages(CancellationToken ct) => Ok(await _service.Voyages(ct));
    [Authorize(Roles = Lines)] [HttpPost("voyages")]
    public async Task<IActionResult> Voyage(CreateExportVoyageRequest request, CancellationToken ct) => Ok(await _service.CreateVoyage(request, ct));
    [Authorize(Roles = Lines)] [HttpGet("voyages/{id:guid}/close-preview")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct) => Ok(await _service.ClosePreview(id, ct));
    [Authorize(Roles = Lines)] [HttpPost("voyages/{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.CloseVoyage(id, request, ct));
    [Authorize(Roles = "Administrator,Admin,Berth Staff")] [HttpPost("voyages/{id:guid}/port-event")]
    public async Task<IActionResult> VoyageEvent(Guid id, ExportPortEventRequest request, CancellationToken ct) => Ok(await _service.VoyageEvent(id, request, ct));
    [Authorize(Roles = Lines)] [HttpGet("voyages/{id:guid}/manifest")]
    public async Task<IActionResult> Manifest(Guid id, CancellationToken ct) => Ok(await _service.Manifest(id, ct));
    [Authorize(Roles = Lines)] [HttpPost("voyages/{id:guid}/manifest")]
    public async Task<IActionResult> BuildManifest(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.BuildManifest(id, request, false, ct));
    [Authorize(Roles = Lines)] [HttpPost("voyages/{id:guid}/manifest/send")]
    public async Task<IActionResult> SendManifest(Guid id, ExportVersionRequest request, CancellationToken ct) => Ok(await _service.BuildManifest(id, request, true, ct));
}

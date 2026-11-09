using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NexusPort.Modules.ServiceCatalog.Application;
using NexusPort.Modules.ServiceCatalog.Domain;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.ServiceCatalog.Presentation;

[ApiController]
[Route("api/v1/ServiceDrafts")]
[Authorize(Roles = "Transport Company,Carrier,Carrier Staff")]
public sealed class ServiceDraftsController(ServiceDraftService service) : ControllerBase
{
    private async Task<IActionResult> Run(Func<Task<object>> action)
    {
        try { return Ok(await action()); }
        catch (AppException error) { return StatusCode(error.StatusCode, new { message = error.Message, errorCode = error.ErrorCode }); }
        catch (PostgresException error) when (error.SqlState is "42P01" or "42703")
        { return StatusCode(503, new { message = "Chức năng dịch vụ chưa đủ cấu trúc dữ liệu. Cần triển khai migration 20261109_037 và các migration dịch vụ/Booking trước đó.", errorCode = "SERVICE_DRAFT_SCHEMA_REQUIRED" }); }
        catch (PostgresException error) when (error.SqlState is "40001" or "40P01")
        { return Conflict(new { message = "Dữ liệu vừa thay đổi trong lúc xác nhận. Kiểm tra lại hồ sơ và biểu phí.", errorCode = "SERVICE_CONFIRMATION_CONFLICT" }); }
        catch (PostgresException error) when (error.SqlState is "23503" or "23505")
        { return Conflict(new { message = "Dữ liệu nguồn vừa thay đổi. Tải lại trước khi lưu bản nháp.", errorCode = "SERVICE_DRAFT_CONFLICT" }); }
    }

    [HttpGet("sources")]
    public Task<IActionResult> Sources([FromQuery] string flow = "EXPORT", [FromQuery] string? search = null, CancellationToken ct = default)
        => Run(() => service.Sources(flow, search, ct));
    [HttpGet("containers")]
    public Task<IActionResult> Containers([FromQuery] string flow = "EXPORT", [FromQuery] Guid? sourceId = null,
        [FromQuery] string? search = null, CancellationToken ct = default)
        => Run(() => service.Containers(flow, sourceId, search, ct));
    [HttpGet]
    public Task<IActionResult> List([FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Run(() => service.List(search, page, pageSize, ct));
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => Run(async () => await service.Get(id, ct));
    [HttpPut("{id:guid}")]
    [RequestSizeLimit(256 * 1024)]
    public Task<IActionResult> Save(Guid id, ServiceDraftInput input, CancellationToken ct)
        => Run(async () => await service.Save(id, input, ct));

    [HttpPost("{id:guid}/validate")]
    public Task<IActionResult> Validate(Guid id, ServiceValidationInput input, CancellationToken ct)
        => Run(async () => await service.ValidateRegistration(id, input, ct));
    [HttpPost("{id:guid}/confirm")]
    public Task<IActionResult> Confirm(Guid id, ServiceConfirmationInput input, CancellationToken ct)
        => Run(async () => await service.ConfirmRegistration(id, input, ct));
    [HttpGet("{id:guid}/registration")]
    public Task<IActionResult> Registration(Guid id, CancellationToken ct)
        => Run(async () => await service.Registration(id, ct));
}

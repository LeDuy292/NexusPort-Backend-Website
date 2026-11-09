using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NexusPort.Modules.ServiceCatalog.Application;
using NexusPort.Modules.ServiceCatalog.Domain;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.ServiceCatalog.Presentation;

[ApiController]
[Route("api/v1/ServiceCatalog")]
[Authorize(Roles = "Administrator,Admin,Transport Company,Carrier,Carrier Staff")]
public sealed class ServiceCatalogController(CatalogService service) : ControllerBase
{
    private const string Admin = "Administrator,Admin";
    private async Task<IActionResult> Run(Func<Task<object>> action)
    {
        try { return Ok(await action()); }
        catch (AppException error) { return StatusCode(error.StatusCode, new { message = error.Message, errorCode = error.ErrorCode }); }
        catch (PostgresException error) when (error.SqlState == "42P01") { return StatusCode(503, new { message = "Danh mục dịch vụ chưa được khởi tạo. Cần triển khai migration 20261108_035.", errorCode = "CATALOG_SCHEMA_REQUIRED" }); }
        catch (PostgresException error) when (error.SqlState == "23505") { return Conflict(new { message = "Mã dịch vụ đã tồn tại. Vui lòng chọn mã khác.", errorCode = "CATALOG_CONFLICT" }); }
    }
    [HttpGet] public Task<IActionResult> List(CancellationToken ct) => Run(() => service.List(ct));
    [HttpPost] [Authorize(Roles = Admin)] public Task<IActionResult> Create(ServiceInput input, CancellationToken ct) => Run(async () => await service.Save(null, input, ct));
    [HttpPut("{id:guid}")] [Authorize(Roles = Admin)] public Task<IActionResult> Update(Guid id, ServiceInput input, CancellationToken ct) => Run(async () => await service.Save(id, input, ct));
    [HttpPost("{id:guid}/tariffs")] [Authorize(Roles = Admin)] public Task<IActionResult> AddTariff(Guid id, TariffInput input, CancellationToken ct) => Run(async () => await service.AddTariff(id, input, ct));
    [HttpPost("{id:guid}/tariffs/{tariffId:guid}/retire")] [Authorize(Roles = Admin)] public Task<IActionResult> Retire(Guid id, Guid tariffId, CancellationToken ct) => Run(async () => await service.Retire(id, tariffId, ct));
    [HttpPost("{id:guid}/quote")] public Task<IActionResult> Quote(Guid id, QuoteInput input, CancellationToken ct) => Run(async () => await service.Quote(id, input, ct));
    [HttpGet("{id:guid}/history")] [Authorize(Roles = Admin)] public Task<IActionResult> History(Guid id, CancellationToken ct) => Run(() => service.History(id, ct));
}

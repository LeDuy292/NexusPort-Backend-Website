using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NexusPort.Modules.ServiceCatalog.Application;
using NexusPort.Modules.ServiceCatalog.Domain;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.ServiceCatalog.Presentation;

[ApiController]
[Route("api/v1/ServiceOrders")]
[Authorize(Roles = "Transport Company,Carrier,Carrier Staff,Administrator,Admin,Dispatcher")]
public sealed class ServiceOrdersController(ServiceOrderService service) : ControllerBase
{
    private async Task<IActionResult> Run(Func<Task<object>> action)
    {
        try { return Ok(await action()); }
        catch (AppException error) { return StatusCode(error.StatusCode, new { message = error.Message, errorCode = error.ErrorCode }); }
        catch (PostgresException error) when (error.SqlState is "42P01" or "42703")
        { return StatusCode(503, new { message = "Chức năng quản lý đơn dịch vụ chưa sẵn sàng. Vui lòng liên hệ quản trị viên.", errorCode = "SERVICE_ORDER_SCHEMA_REQUIRED" }); }
        catch (PostgresException error) when (error.SqlState is "23505" or "23503" or "23514" or "40001" or "40P01")
        { return Conflict(new { message = "Dữ liệu vừa thay đổi hoặc yêu cầu đã được xử lý. Tải lại đơn và kiểm tra trạng thái trước khi thao tác.", errorCode = "SERVICE_ORDER_CONFLICT" }); }
    }

    [HttpGet]
    public Task<IActionResult> List([FromQuery] string? search = null, [FromQuery] string? status = null,
        [FromQuery] string? paymentStatus = null, [FromQuery] string? flow = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Run(() => service.OrdersList(search, status, paymentStatus, flow, page, pageSize, ct));
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Detail(Guid id, CancellationToken ct) => Run(async () => await service.GetOrder(id, ct));
    [HttpGet("{id:guid}/receipt")]
    public Task<IActionResult> Receipt(Guid id, [FromQuery] Guid? sourceContainerId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, private";
        return Run(async () => await service.RegistrationReceipt(id, ct, sourceContainerId: sourceContainerId));
    }
    [HttpGet("{id:guid}/history")]
    public Task<IActionResult> History(Guid id, CancellationToken ct) => Run(() => service.OrderHistory(id, ct));
    [HttpPost("{id:guid}/actions")]
    [RequestSizeLimit(16 * 1024)]
    public Task<IActionResult> Action(Guid id, ServiceOrderActionInput input, CancellationToken ct)
        => Run(async () => await service.OrderAction(id, input, ct));
    [HttpGet("{id:guid}/payments")]
    public Task<IActionResult> Payments(Guid id, CancellationToken ct) => Run(() => service.ListServicePayments(id, ct));
    [HttpPost("{id:guid}/payments")]
    public Task<IActionResult> CreatePayment(Guid id, ServicePaymentCreateInput input, CancellationToken ct)
        => Run(() => service.CreateServicePayment(id, input, ct));
    [HttpPost("{id:guid}/payments/{paymentId:guid}/sync")]
    public Task<IActionResult> SyncPayment(Guid id, Guid paymentId, ServicePaymentSyncInput input, CancellationToken ct)
        => Run(() => service.SyncServicePayment(id, paymentId, input, ct));
    [HttpPost("{id:guid}/payments/{paymentId:guid}/cancel")]
    public Task<IActionResult> CancelPayment(Guid id, Guid paymentId, ServicePaymentCancelInput input, CancellationToken ct)
        => Run(() => service.CancelServicePayment(id, paymentId, input, ct));
    [AllowAnonymous]
    [HttpPost("payments/payos/webhook")]
    [RequestSizeLimit(32 * 1024)]
    public Task<IActionResult> Webhook(JsonObject input, CancellationToken ct)
        => Run(() => service.HandlePayOsWebhook(input, ct));
}

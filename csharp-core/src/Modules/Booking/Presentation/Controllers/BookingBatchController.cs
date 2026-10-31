using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Presentation.Controllers;

[ApiController]
[Authorize(Roles="Administrator,Admin,Dispatcher,Carrier,Transport Company,Carrier Staff")]
[Route("api/v1/Booking/batches")]
public sealed class BookingBatchController:ControllerBase
{
    private readonly IBookingService _service;
    private readonly AppDbContext _context;
    private readonly ICurrentUser _user;
    public BookingBatchController(IBookingService service,AppDbContext context,ICurrentUser user){_service=service;_context=context;_user=user;}
    private bool IsCarrier=>new[]{"Carrier","Transport Company","Carrier Staff"}.Contains(_user.Role,StringComparer.OrdinalIgnoreCase);
    private async Task<Guid?> Scope(CancellationToken token)
    {
        if(!_user.UserId.HasValue)throw new UnauthorizedException("Vui lòng đăng nhập.");
        if(!IsCarrier)return null;
        var memberships=await _context.Database.SqlQueryRaw<Guid>("SELECT carrier_id AS \"Value\" FROM carrier_users WHERE user_id={0}",_user.UserId.Value).ToListAsync(token);
        if(memberships.Count!=1)throw new UnauthorizedException("Tài khoản cần thuộc đúng một công ty vận tải.");
        return memberships[0];
    }
    [HttpPost("plan")]
    [EnableRateLimiting("booking-create")]
    public async Task<IActionResult> Plan([FromBody]BookingBatchRequest request,CancellationToken token)
    {
        request.CarrierId=await Scope(token)??request.CarrierId;
        return Ok(await _service.PlanBatchAsync(request,token));
    }
    [HttpGet("container-options")]
    public async Task<IActionResult> Options([FromQuery]string bookingType="Dropoff",[FromQuery]Guid? containerId=null,
        [FromQuery]Guid? declarationId=null,[FromQuery]string? yardCode=null,CancellationToken token=default)
    {
        var carrier=await Scope(token);
        if(!carrier.HasValue)throw new ValidationException("CarrierId","Chọn container bằng tài khoản công ty vận tải để lập lô; tài khoản điều độ có thể xem và xử lý các chuyến đã tạo.");
        var anchor=containerId.HasValue?new BookingPlanningLeg{ContainerId=containerId.Value,DeclarationId=declarationId,YardCode=yardCode,BookingType=bookingType}:null;
        return Ok(await _service.GetBatchContainerOptionsAsync(carrier.Value,bookingType,anchor,token));
    }
    [HttpPost]
    [EnableRateLimiting("booking-create")]
    public async Task<IActionResult> Create([FromBody]BookingBatchRequest request,CancellationToken token)
    {
        request.CarrierId=await Scope(token)??request.CarrierId;
        request.CreatedBy=_user.UserId;
        var result=await _service.CreateBatchAsync(request,token);
        return CreatedAtAction(nameof(Detail),new{id=result.Id},result);
    }
    [HttpGet]
    public async Task<IActionResult> List([FromQuery]int page=1,CancellationToken token=default)
        =>Ok(await _service.GetBatchesAsync(await Scope(token),page,token));
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id,CancellationToken token)
        =>Ok(await _service.GetBatchAsync(id,await Scope(token),token));
}

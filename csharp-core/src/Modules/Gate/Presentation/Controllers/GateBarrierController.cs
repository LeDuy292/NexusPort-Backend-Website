using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Domain.Entities;
using NexusPort.Modules.Gate.Application.Services;
using NexusPort.Modules.Gate.Domain.Entities;
using NexusPort.Shared.Exceptions;
using ValidationException = NexusPort.Shared.Exceptions.ValidationException;
using BookingEntity = NexusPort.Modules.Booking.Domain.Entities.Booking;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Interfaces;

namespace NexusPort.Modules.Gate.Presentation.Controllers;

[ApiController]
[Authorize(Roles="Administrator,Admin,Gate Officer,Gate")]
[Route("api/v1/Gate")]
public class GateBarrierController(AppDbContext context, ICurrentUser user, IBookingService bookings) : ControllerBase
{
    // Read-only gate calendar. Booking creation/approval permissions stay on BookingController.
    [HttpGet("bookings")]
    public async Task<IActionResult> Bookings([FromQuery] BookingFilterParams filter,CancellationToken token)
    {
        filter.PageSize=Math.Clamp(filter.PageSize,1,150);
        return Ok(await bookings.GetPagedAsync(filter,token));
    }

    [HttpGet("entry-orders")]
    public async Task<IActionResult> Orders(CancellationToken token)
    {
        var now=DateTime.UtcNow;
        var orders=await new GateEntryAccess(context).QueueAsync(token);
        foreach(var order in orders)
            if ((order.Status=="active" || order.Status=="pending_ack") && GateEntryAccess.Utc(order.ValidUntil)<=now) order.Status="expired";
        return Ok(new {items=orders,serverTime=now});
    }

    // RequestId is an idempotency key. The simulator never claims hardware acknowledgment.
    [HttpPost("barrier-commands")]
    public async Task<IActionResult> Command(BarrierCommandRequest request, CancellationToken token)
    {
        if(!user.UserId.HasValue) return Unauthorized();
        if(request.RequestId==Guid.Empty || (request.Action!="open" && request.Action!="close") ||
          (request.Source!="automatic" && request.Source!="manual"))
            throw new ValidationException("Command","Cần mã yêu cầu, thao tác open/close và nguồn automatic/manual hợp lệ.");
        if(request.Source=="manual" && string.IsNullOrWhiteSpace(request.Reason))
            throw new ValidationException("Reason","Nhập lý do điều khiển cổng thủ công.");
        var gateCode=request.GateCode.Trim().ToUpperInvariant();
        await using var transaction=await context.Database.BeginTransactionAsync(token);
        // Serialize commands for one gate, including retries with the same request ID.
        var gates=await context.Database.SqlQueryRaw<BarrierGate>(
          "SELECT id AS \"Id\",CASE WHEN code IN ('GATE-02','GATE-03') THEN 'gate_out' ELSE 'gate_in' END AS \"Type\" FROM gates WHERE code={0} AND code IN ('GATE-01','GATE-02','GATE-03') AND is_active FOR UPDATE",gateCode).ToListAsync(token);
        if(gates.Count!=1) throw new ValidationException("Gate","Cổng không tồn tại hoặc đang ngừng hoạt động.");
        var previous=await context.Database.SqlQueryRaw<BarrierCommandResult>(CommandSelect+" WHERE id={0}",request.RequestId).FirstOrDefaultAsync(token);
        if(previous!=null)
        {
            if(previous.GateCode!=gateCode || previous.Action!=request.Action || previous.RequestedBy!=user.UserId ||
              previous.Source!=request.Source || previous.VerificationId!=request.VerificationRecordId)
                throw new ValidationException("RequestId","Mã yêu cầu đã được dùng cho lệnh khác.");
            await transaction.CommitAsync(token);
            return Ok(previous);
        }
        Guid? orderId=null;
        if(request.Action=="open")
        {
            if(!request.VerificationRecordId.HasValue) throw new ValidationException("Verification","Cần xác minh xe trước khi mở cổng.");
            var record=await context.Set<GateVerificationRecord>().FirstOrDefaultAsync(r=>r.Id==request.VerificationRecordId.Value,token);
            if(record==null || record.VerificationStatus!="PASS" || record.GateCode!=gateCode || !record.BookingId.HasValue ||
              !GateEntryAccess.IsFreshVerification(record.VerificationTime,DateTime.UtcNow))
                throw new ValidationException("Verification","Cần kết quả xác minh PASS mới trong 2 phút tại đúng cổng.");
            var outScan=record.VerificationType.Contains("OUT",StringComparison.OrdinalIgnoreCase);
            if(outScan!=(gates[0].Type=="gate_out")) throw new ValidationException("Gate","Chiều xác minh không khớp cổng.");
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT b.id FROM bookings b JOIN drivers d ON d.id=b.driver_id WHERE b.id={record.BookingId.Value} FOR UPDATE OF b,d",token);
            var booking=await context.Set<BookingEntity>().FirstAsync(b=>b.Id==record.BookingId.Value,token);
            if(!outScan)
            {
                var order=await new GateEntryAccess(context).LatestAsync(booking.Id,token);
                var check=GateEntryAccess.Check(order,booking,record.DetectedPlate,DateTime.UtcNow);
                if(!check.IsSuccess) throw new ValidationException("EntryOrder",check.Message);
                orderId=order!.Id;
            }
            else
            {
                // Preserve the existing Node gate-out checks; a PASS OCR record alone is insufficient.
                var ready=await context.Database.SqlQueryRaw<int>(@"SELECT count(*)::int AS ""Value"" FROM bookings b
                  WHERE b.id={0} AND b.status::text='completed'
                  AND EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_in' AND g.status='completed')
                  AND NOT EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_out' AND g.status='completed')
                  AND EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.booking_id=b.id)
                  AND NOT EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.booking_id=b.id AND NOT EXISTS
                    (SELECT 1 FROM container_confirmation_audits a WHERE a.booking_id=b.id AND a.container_id=bc.container_id))",booking.Id).SingleAsync(token);
                if(ready!=1) throw new ValidationException("GateOut","Xe chưa đủ điều kiện ra cổng.");
            }
        }
        // Simulation adapter for this release. A future IoT worker consumes requested
        // commands by ID and records a device acknowledgment; it does not create Gate In.
        await context.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO gate_barrier_commands
          (id,gate_id,gate_code,action,status,source,verification_id,entry_order_id,requested_by,reason)
          VALUES({request.RequestId},{gates[0].Id},{gateCode},{request.Action},'simulated',{request.Source},
            {request.VerificationRecordId},{orderId},{user.UserId},{request.Reason})",token);
        var result=await context.Database.SqlQueryRaw<BarrierCommandResult>(CommandSelect+" WHERE id={0}",request.RequestId).SingleAsync(token);
        await transaction.CommitAsync(token);
        return Ok(result);
    }

    [HttpGet("barrier-commands")]
    public async Task<IActionResult> History([FromQuery]string gateCode,CancellationToken token) => Ok(await context.Database
      .SqlQueryRaw<BarrierCommandResult>(CommandSelect+" WHERE gate_code={0} ORDER BY requested_at DESC LIMIT 50",gateCode.Trim().ToUpperInvariant()).ToListAsync(token));

    private const string CommandSelect=@"SELECT id AS ""CommandId"",gate_code AS ""GateCode"",action AS ""Action"",status AS ""Status"",
      source AS ""Source"",verification_id AS ""VerificationId"",requested_by AS ""RequestedBy"",requested_at AS ""RequestedAt"" FROM gate_barrier_commands";
}

public class BarrierCommandRequest
{
    public Guid RequestId {get;set;}
    [Required,MaxLength(50)] public string GateCode {get;set;}=string.Empty;
    [Required] public string Action {get;set;}=string.Empty;
    public string Source {get;set;}="automatic";
    public Guid? VerificationRecordId {get;set;}
    [MaxLength(500)] public string? Reason {get;set;}
}
public class BarrierGate {public Guid Id {get;set;} public string Type {get;set;}=string.Empty;}
public class BarrierCommandResult
{
    public Guid CommandId {get;set;}
    public string GateCode {get;set;}=string.Empty;
    public string Action {get;set;}=string.Empty;
    public string Status {get;set;}=string.Empty;
    public string Source {get;set;}=string.Empty;
    public Guid? VerificationId {get;set;}
    public Guid RequestedBy {get;set;}
    public DateTime RequestedAt {get;set;}
    public string PhysicalState => "unknown";
}

using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Modules.Gate.Application.Services;
using NexusPort.Modules.Gate.Domain.Entities;
using NexusPort.Modules.Gate.Domain.Rules;
using NexusPort.Modules.Gate.Domain.Rules.Concrete;
using NexusPort.Modules.Gate.Presentation.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

var count=0;
void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);count++;}
var now=DateTime.UtcNow;
var booking=new NexusPort.Modules.Booking.Domain.Entities.Booking{
    DriverId=Guid.NewGuid(),TruckId=Guid.NewGuid(),Status=BookingStatus.Approved,
    AppointmentStart=now.AddHours(6),AppointmentEnd=now.AddHours(7),GateType="GateIn"};
var order=new GateEntryOrder{Id=Guid.NewGuid(),BookingId=booking.Id,DriverId=booking.DriverId.Value,
    TruckId=booking.TruckId,VehiclePlate="43C12345",Status="active",ValidFrom=now.AddMinutes(-1),ValidUntil=now.AddMinutes(14)};
bool Allowed()=>GateEntryAccess.Check(order,booking,"43C-123.45",now).IsSuccess;
Check(Allowed(),"active dispatcher permission admits truck before appointment");
Check(!GateEntryAccess.Check(null,booking,"43C-123.45",now).IsSuccess,"no permission denies entry");
foreach(var state in new[]{"pending_ack","expired","revoked","consumed"}){
    order.Status=state;Check(!Allowed(),state+" cannot open the gate");
}
order.Status="active";
Check(!GateEntryAccess.Check(order,booking,"43C-999.99",now).IsSuccess,"wrong plate denied");
Check(!GateEntryAccess.Check(order,booking,"43C12345",order.ValidUntil).IsSuccess,"exact expiry boundary denied");
Check(!GateEntryAccess.Check(order,booking,"43C12345",order.ValidFrom.AddSeconds(-1)).IsSuccess,"before activation denied");
order.Status="pending_ack";
Check(GateEntryAccess.Check(order,booking,"43C12345",order.ValidUntil).FailureReason=="ENTRY_ORDER_UNAVAILABLE","expired acknowledgment is reported as expired");
order.Status="active";
order.DriverId=Guid.NewGuid();Check(!Allowed(),"changed driver denied");order.DriverId=booking.DriverId.Value;
order.TruckId=Guid.NewGuid();Check(!Allowed(),"changed truck denied");order.TruckId=booking.TruckId;
booking.Status=BookingStatus.Canceled;Check(!Allowed(),"canceled booking denied");booking.Status=BookingStatus.Approved;
var ctx=new GateValidationContext{GateType="GateOut",Booking=booking,VerificationTime=now.AddDays(1)};
Check((await new BookingTimeWindowRule().EvaluateAsync(ctx)).IsSuccess,"exit is not constrained by entry appointment time");
Check((await new OperationMatchRule().EvaluateAsync(ctx)).IsSuccess,"same booking supports in and out");
booking.Status=BookingStatus.Completed;
Check((await new BookingExistenceAndStatusRule().EvaluateAsync(ctx)).IsSuccess,"completed operation may exit");
ctx.GateType="GateIn";
Check(!(await new BookingExistenceAndStatusRule().EvaluateAsync(ctx)).IsSuccess,"completed operation cannot re-enter");
var auth=typeof(GateBarrierController).GetCustomAttributes(typeof(AuthorizeAttribute),true).Cast<AuthorizeAttribute>().Single();
Check(auth.Roles!.Contains("Gate Officer")&&!auth.Roles.Contains("Driver"),"barrier API restricted to gate staff/admin");
Check(typeof(GateBarrierController).GetMethod("Command")!.GetCustomAttributes(typeof(HttpPostAttribute),true).Cast<HttpPostAttribute>().Single().Template=="barrier-commands","explicit barrier command endpoint");
Check(typeof(GateBarrierController).GetMethod("Bookings")!.GetCustomAttributes(typeof(HttpGetAttribute),true).Cast<HttpGetAttribute>().Single().Template=="bookings","gate staff use their dedicated read-only booking endpoint");
var response=new BarrierCommandResult{Status="simulated"};
Check(response.PhysicalState=="unknown","simulator does not claim hardware state");
Check(!new NexusPort.Modules.Gate.Application.DTOs.GateInApprovalRequestDto().PassageConfirmed,"opening alone cannot confirm passage");
await using var noDatabase=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options);
var entryRule=new GateEntryOrderRule(noDatabase);
ctx.GateType="GateIn";ctx.GateCode="GATE-02";
Check(!(await entryRule.EvaluateAsync(ctx)).IsSuccess,"retired second entrance denied before DB access");
ctx.GateType="GateOut";ctx.GateCode="GATE-01";
Check(!(await entryRule.EvaluateAsync(ctx)).IsSuccess,"entrance cannot be used as exit");
var actorId=Guid.NewGuid();
booking.Status=BookingStatus.Approved;
booking.VehiclePlate="29-Y3036.58";
order.VehiclePlate="29Y303658";
foreach(var plate in new[]{"29Y3-036.58","29-Y3036.58","29Y303658"}){
    Check(GateEntryAccess.Check(order,booking,plate,now).IsSuccess,"entry permission ignores separators: "+plate);
    Check((await new VehicleMatchAndStatusRule().EvaluateAsync(new GateValidationContext{
        Booking=booking,VehiclePlate=plate,VehicleDetected=true})).IsSuccess,"booking plate identity ignores separators: "+plate);
}
Check(!(await new VehicleMatchAndStatusRule().EvaluateAsync(new GateValidationContext{
    Booking=booking,VehiclePlate="29Y3-036.59",VehicleDetected=true})).IsSuccess,"different plate digits are still rejected");
var utcFrom=order.ValidFrom;
var utcUntil=order.ValidUntil;
order.ValidFrom=utcFrom.ToLocalTime();order.ValidUntil=utcUntil.ToLocalTime();
Check(GateEntryAccess.Check(order,booking,"29Y3-036.58",now).IsSuccess,"Npgsql legacy local timestamps admit the same active permission as UTC timestamps");
Check(!GateEntryAccess.Check(order,booking,"29Y3-036.58",utcUntil).IsSuccess,"local permission expires exactly at the UTC deadline");
order.Status="pending_ack";
Check(GateEntryAccess.Check(order,booking,"29Y3-036.58",now).FailureReason=="ENTRY_ACK_REQUIRED","local timestamps still require driver acknowledgment");
order.Status="active";order.ValidFrom=utcFrom;order.ValidUntil=utcUntil;
Check(GateEntryAccess.IsFreshVerification(now.AddMinutes(-1).ToLocalTime(),now),"recent local verification is fresh");
Check(!GateEntryAccess.IsFreshVerification(now.AddMinutes(-3).ToLocalTime(),now),"stale local verification cannot open barrier");
Check(!GateEntryAccess.IsFreshVerification(now.AddMinutes(1).ToLocalTime(),now),"future verification cannot open barrier");
var http=new DefaultHttpContext{User=new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim("id",actorId.ToString())},"test"))};
Check(new CurrentUser(new HttpContextAccessor{HttpContext=http}).UserId==actorId,"develop JWT id-claim fallback retained");
Console.WriteLine($"PASS {count} gate flow checks (no database writes)");

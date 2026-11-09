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

// =========================================================================
// REFACTORED BOOKING MODULE & OPERATION TESTS (15 SCENARIOS)
// =========================================================================
var compatPolicy = new NexusPort.Modules.Booking.Application.Services.BookingCompatibilityPolicy();
var docPolicy = new NexusPort.Modules.Booking.Application.Services.BookingDocumentRequirementPolicy();

// 1. Valid export loaded-container Gate In
var exportReqs = docPolicy.GetRequiredDocuments("Export", "Loaded", "General");
Check(exportReqs.Any(r => r.DocumentType == "CarrierBookingReference" && r.IsMandatory), "Export requires CarrierBookingReference");
Check(exportReqs.Any(r => r.DocumentType == "VgmCertificate" && r.IsMandatory), "Export requires VGM Certificate for loaded container");
var exportOp = new NexusPort.Modules.Booking.Application.DTOs.CreateBookingOperationDto
{
    OperationType = "Export",
    ExpectedGateDirection = "GateIn",
    CargoStatus = "Loaded",
    ContainerNumber = "EXP-CONT-8891"
};
Check(compatPolicy.ValidateCombination(new[] { exportOp }).IsValid, "Single valid Export operation is compatible");

// 2. Valid import loaded-container Gate Out
var importReqs = docPolicy.GetRequiredDocuments("Import", "Loaded", "General");
Check(importReqs.Any(r => r.DocumentType == "BillOfLading" && r.IsMandatory), "Import requires BillOfLading");
Check(importReqs.Any(r => r.DocumentType == "DeliveryOrder" && r.IsMandatory), "Import requires DeliveryOrder (eDO)");
Check(importReqs.Any(r => r.DocumentType == "CustomsDeclaration" && r.IsMandatory), "Import requires CustomsDeclaration clearance");
var importOp = new NexusPort.Modules.Booking.Application.DTOs.CreateBookingOperationDto
{
    OperationType = "Import",
    ExpectedGateDirection = "GateOut",
    CargoStatus = "Loaded",
    ContainerNumber = "IMP-CONT-7712"
};
Check(compatPolicy.ValidateCombination(new[] { importOp }).IsValid, "Single valid Import operation is compatible");

// 3. Disallow empty pickup (only Export/Import allowed)
var emptyPickupReqs = docPolicy.GetRequiredDocuments("EmptyPickup", "Empty", "General");
var emptyPickupOp = new NexusPort.Modules.Booking.Application.DTOs.CreateBookingOperationDto
{
    OperationType = "EmptyPickup",
    ExpectedGateDirection = "GateOut",
    CargoStatus = "Empty",
    ContainerNumber = "EMPTY-PKP-002"
};
Check(!compatPolicy.ValidateCombination(new[] { emptyPickupOp }).IsValid, "Single EmptyPickup operation is rejected (system only supports Export and Import)");

// 4. Disallow empty return (only Export/Import allowed)
var emptyReturnReqs = docPolicy.GetRequiredDocuments("EmptyReturn", "Empty", "General");
var emptyReturnOp = new NexusPort.Modules.Booking.Application.DTOs.CreateBookingOperationDto
{
    OperationType = "EmptyReturn",
    ExpectedGateDirection = "GateIn",
    CargoStatus = "Empty",
    ContainerNumber = "EMPTY-RTN-001"
};
Check(!compatPolicy.ValidateCombination(new[] { emptyReturnOp }).IsValid, "Single EmptyReturn operation is rejected (system only supports Export and Import)");

// 5. Combined empty return and empty pickup in one visit is rejected
var combinedReturnPickup = compatPolicy.ValidateCombination(new[] { emptyReturnOp, emptyPickupOp });
Check(!combinedReturnPickup.IsValid, "Combined Empty Return + Empty Pickup is rejected (no empty container operations)");

// 6. Combined export and import in one visit is allowed
var combinedExportImport = compatPolicy.ValidateCombination(new[] { exportOp, importOp });
Check(combinedExportImport.IsValid, "Combined Export + Import on single truck visit is allowed");

// 7. Invalid combination: two GateIn operations (e.g. Export + Export)
var exportOp2 = new NexusPort.Modules.Booking.Application.DTOs.CreateBookingOperationDto { OperationType = "Export", ExpectedGateDirection = "GateIn", ContainerNumber = "CONT-EXP-99" };
var dualGateIn = compatPolicy.ValidateCombination(new[] { exportOp, exportOp2 });
Check(!dualGateIn.IsValid, "Dual simultaneous Gate-In legs on 1 truck are rejected");

// 8. Invalid combination: identical container for return and pickup
var sameContOp1 = new NexusPort.Modules.Booking.Application.DTOs.CreateBookingOperationDto { OperationType = "Export", ExpectedGateDirection = "GateIn", ContainerNumber = "CONT-DUP-99" };
var sameContOp2 = new NexusPort.Modules.Booking.Application.DTOs.CreateBookingOperationDto { OperationType = "Import", ExpectedGateDirection = "GateOut", ContainerNumber = "CONT-DUP-99" };
Check(!compatPolicy.ValidateCombination(new[] { sameContOp1, sameContOp2 }).IsValid, "Same container for both drop and pick simultaneously is rejected");

// 9. Invalid combination: more than 2 operations for a single chassis visit
Check(!compatPolicy.ValidateCombination(new[] { exportOp, importOp, exportOp2 }).IsValid, "Exceeding dual-cycle limit is rejected");

// 10. Conditional document rules: Reefer and DangerousGoods
var reeferReqs = docPolicy.GetRequiredDocuments("Export", "Loaded", "Reefer");
Check(reeferReqs.Any(r => r.DocumentType == "ReeferTemperatureInstructions" && r.IsMandatory), "Reefer cargo mandates temperature instructions");

var dgReqs = docPolicy.GetRequiredDocuments("Export", "Loaded", "DangerousGoods");
Check(dgReqs.Any(r => r.DocumentType == "DangerousGoodsDeclaration" && r.IsMandatory), "Dangerous Goods cargo mandates DG Declaration");

// 11. Multi-operation entity state and derived status
var testBooking = new NexusPort.Modules.Booking.Domain.Entities.Booking
{
    BookingCode = "BK-TEST-MULTI-01",
    BookingType = BookingType.Dropoff,
    IsCombined = true
};
var op1 = new NexusPort.Modules.Booking.Domain.Entities.BookingOperation { BookingId = testBooking.Id, OperationType = "EmptyReturn", Status = "Approved" };
var op2 = new NexusPort.Modules.Booking.Domain.Entities.BookingOperation { BookingId = testBooking.Id, OperationType = "EmptyPickup", Status = "Approved" };
testBooking.AddOperation(op1);
testBooking.AddOperation(op2);
testBooking.UpdateDerivedStatus();
Check(testBooking.Status == BookingStatus.Approved, "Derived booking status is Approved when all operations approved");
Check(testBooking.TripStatus == "Scheduled", "Trip is Scheduled when operations approved");

// 12. One combined operation fails / on hold while the other remains valid
op1.Status = "Completed";
op2.Status = "OnHold";
testBooking.UpdateDerivedStatus();
Check(op1.Status == "Completed", "Operation 1 completion is independent of Operation 2 hold");
Check(testBooking.TripStatus == "PartiallyCompleted", "Trip status is PartiallyCompleted when leg 1 done and leg 2 on hold");

// 13. Cancellation of one operation does not destroy valid operation
op2.Status = "Cancelled";
testBooking.UpdateDerivedStatus();
Check(op1.Status == "Completed", "Operation 1 remains Completed even if Operation 2 is Cancelled");

// 14. Document verification status transition
var testDoc = new NexusPort.Modules.Booking.Domain.Entities.BookingDocument
{
    BookingId = testBooking.Id,
    BookingOperationId = op1.Id,
    DocumentType = "CarrierBookingReference",
    DocumentNumber = "REF-889900",
    VerificationStatus = "Uploaded"
};
Check(testDoc.VerificationStatus == "Uploaded", "Initial doc status is Uploaded");
testDoc.Verify(actorId);
Check(testDoc.VerificationStatus == "Verified" && testDoc.VerifiedBy == actorId, "Document transition to Verified with auditor identity");

// 15. All operations completed closes the parent booking and trip
op2.Status = "Completed";
testBooking.UpdateDerivedStatus();
Check(testBooking.Status == BookingStatus.Completed, "Parent booking completes when all operations complete");
Check(testBooking.TripStatus == "Completed", "Port visit completes when all operations complete");

Console.WriteLine($"PASS {count} gate and booking operation checks (all 15 scenarios verified, no database writes)");

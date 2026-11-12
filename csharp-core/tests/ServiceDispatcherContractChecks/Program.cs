using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Services;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Modules.ServiceCatalog.Application;
using NexusPort.Modules.ServiceCatalog.Domain;
var connection=Environment.GetEnvironmentVariable("FLOW_FILTER_TEST_CONNECTION")??throw new Exception("Fixture required");var target=new NpgsqlConnectionStringBuilder(connection);
if(target.Host!="127.0.0.1"||target.Port!=55449||target.Database!="flow_filter_check"||target.Username!="filter_check")throw new Exception("Refusing shared DB");
await using var pg=new NpgsqlConnection(connection);await pg.OpenAsync();
await using(var marker=new NpgsqlCommand("SELECT current_setting('nexusport.filter_test',true)",pg))if((string?)await marker.ExecuteScalarAsync()!=Environment.GetEnvironmentVariable("FLOW_FILTER_TEST_MARKER"))throw new Exception("Marker mismatch");
await RegistrationFixtures.Prepare(pg);
await DraftFixtures.Sql(pg,"ALTER TABLE users ADD COLUMN email text;UPDATE users SET email='fixture@example.invalid';INSERT INTO users(id,role,is_active,email) VALUES(@a,'Dispatcher',true,'staff@example.invalid');UPDATE containers SET container_no='MSCU6639870' WHERE id=@b;UPDATE export_booking_containers SET container_no='MSCU6639870' WHERE container_id=@b;DELETE FROM eport_tariffs",DraftFixtures.Id(9),DraftFixtures.Id(501));
foreach(var file in new[]{"orders.sql","email.sql","billing.sql","billing.sql"})await DraftFixtures.Sql(pg,await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,file)));
await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
var mail=new Mail();var gateway=new Gateway();var fleet=new Fleet();var drafts=new ServiceDraftService(db,new Customer(),mail,fleetAvailability:fleet);var orders=new ServiceOrderService(db,new Customer(),gateway);var dispatcher=new ServiceOrderService(db,new Customer(9,"Dispatcher"),gateway);
JsonNode J(object value)=>JsonSerializer.SerializeToNode(value,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
int checks=0;void Check(bool yes,string message){if(!yes)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
async Task MustReject(Func<Task> action,string message){try{await action();}catch(CatalogException){Check(true,message);return;}throw new Exception(message);}
var before=await DraftFixtures.Fingerprint(pg);
foreach(var flow in new[]{"IMPORT","EXPORT"}){
 var id=Guid.NewGuid();var input=new ServiceDraftInput{WorkflowVersion=2,Flow=flow,MainServiceId=DraftFixtures.Service(flow=="IMPORT"?2:1),Source=flow=="IMPORT"?new(){DocumentType="DO",ReferenceNumber="DO-TEST",BlNumber="BILL-TEST",VesselName="TEST VESSEL",VoyageNumber="V001",ValidUntil=DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd")}:new(){BookingId=DraftFixtures.Id(201)},PlannedAt=DateTimeOffset.UtcNow.AddDays(1).ToString("O"),ContactName="Fixture",ContactPhone="0901234567",Delivery=new(){TruckPlate="15H12345",DriverName="Driver",DriverPhone="0901234567"},Lines=[new(){SourceContainerId=DraftFixtures.Id(flow=="IMPORT"?401:301),Declaration=flow=="IMPORT"?null:new(){ContainerNumber="MSCU6639870",IsoCode="22G1",SealNumber="SEAL-A",WeightKg=24000,OperatorName="LINE",CargoType="General",DischargePort="SGSIN",DestinationPort="SGSIN"}}]};
 input=input with{Delivery=input.Delivery with{Trips=[new(){SourceContainerId=input.Lines[0].SourceContainerId,AppointmentStart=input.PlannedAt,AppointmentEnd=DateTimeOffset.Parse(input.PlannedAt!).AddHours(2).ToString("O"),TruckId=DraftFixtures.Id(9011),DriverId=DraftFixtures.Id(9021)}]}};
 if(flow=="IMPORT"){
  var first=input.Delivery.Trips[0];var second=first with{SourceContainerId=DraftFixtures.Id(403)};var multi=input with{Lines=[input.Lines[0],new(){SourceContainerId=second.SourceContainerId}],Delivery=input.Delivery with{Trips=[first,second]}};
  var plan=J(await drafts.RecommendServiceFleet(multi,default));
  Check(plan["items"]![0]!["recommendedTruckId"]!.ToString()!=plan["items"]![1]!["recommendedTruckId"]!.ToString(),"Overlapping containers receive distinct trucks");
  Check(plan["items"]![0]!["recommendedDriverId"]!.ToString()!=plan["items"]![1]!["recommendedDriverId"]!.ToString(),"Overlapping containers receive distinct drivers");
  Check(plan["items"]![0]!["recommendedDriverId"]!.ToString()==DraftFixtures.Id(9021).ToString(),"Prioritizes the truck's assigned driver");
  await MustReject(async()=>{await drafts.Save(Guid.NewGuid(),multi,default);},"Cannot save overlapping trips with the same fleet");
  var sequential=multi with{Delivery=multi.Delivery with{Trips=[first,second with{AppointmentStart=first.AppointmentEnd,AppointmentEnd=DateTimeOffset.Parse(first.AppointmentEnd!).AddHours(2).ToString("O")}]}};
  await drafts.Save(Guid.NewGuid(),sequential,default);Check(true,"Non-overlapping trips may reuse the same truck and driver");
  await MustReject(async()=>{await drafts.Save(Guid.NewGuid(),input with{Delivery=input.Delivery with{Trips=[first with{TruckId=Guid.NewGuid()}]}},default);},"Foreign or unavailable truck cannot be saved");
  await MustReject(async()=>{await drafts.Save(Guid.NewGuid(),input with{Delivery=input.Delivery with{Trips=[first with{DriverId=Guid.NewGuid()}]}},default);},"Foreign or unavailable driver cannot be saved");
  await MustReject(async()=>{await drafts.RecommendServiceFleet(input with{Delivery=input.Delivery with{Trips=[first with{SourceContainerId=DraftFixtures.Id(402)}]}},default);},"Fleet request cannot substitute an unselected container");
  await MustReject(async()=>{await drafts.RecommendServiceFleet(input with{Lines=[new(){SourceContainerId=DraftFixtures.Id(402)}],Delivery=input.Delivery with{Trips=[first with{SourceContainerId=DraftFixtures.Id(402)}]}},default);},"Fleet request enforces source company access");
  await MustReject(async()=>{await drafts.RecommendServiceFleet(input with{Delivery=input.Delivery with{Trips=[first with{AppointmentEnd=DateTimeOffset.Parse(first.AppointmentStart!).AddHours(25).ToString("O")}]}},default);},"Rejects trip windows over 24 hours");
  var forged=input with{Delivery=input.Delivery with{Trips=[first with{Vehicle=new(first.TruckId!.Value,"FORGED","", "",null,null,null),Driver=new(first.DriverId!.Value,"FORGED",null,"","",null,null,null)}]}};
  var captured=await drafts.Save(Guid.NewGuid(),forged,default);
  Check(captured["delivery"]!["trips"]![0]!["vehicle"]!["plateNumber"]!.ToString()=="15H12345"&&captured["delivery"]!["trips"]![0]!["driver"]!["fullName"]!.ToString()=="Fixture driver 1","Client profile labels cannot override authoritative fleet snapshots");
 }
 var challenge=J(await drafts.SendRegistrationEmail(id,input,default));await drafts.VerifyRegistrationEmail(id,new(Guid.Parse(challenge["challengeId"]!.ToString()),mail.Code),default);
 var customs=J(await drafts.ConfirmRegistrationCustoms(id,input,default));input=input with{Delivery=input.Delivery with{CustomsConfirmationScope=customs["confirmationScope"]!.ToString()}};
 var saved=await drafts.Save(id,input,default);var review=await drafts.ValidateRegistration(id,new(){ExpectedVersion=saved["version"]!.GetValue<int>()},default);
 Check(review["canConfirm"]!.GetValue<bool>()&&review["total"]==null&&review["fees"]!.AsArray().Count==0,flow+" can submit without tariffs and exposes no calculated fees");
 fleet.Busy=true;
 var busyReview=await drafts.ValidateRegistration(id,new(){ExpectedVersion=saved["version"]!.GetValue<int>()},default);
 Check(!busyReview["canConfirm"]!.GetValue<bool>(),flow+" revalidates fleet availability after saving");
 await MustReject(async()=>{await drafts.ConfirmRegistration(id,new(){ExpectedVersion=saved["version"]!.GetValue<int>(),QuoteToken=review["quoteToken"]!.ToString()},default);},flow+" cannot submit a vehicle that became busy");
 fleet.Busy=false;
 var submitted=await drafts.ConfirmRegistration(id,new(){ExpectedVersion=saved["version"]!.GetValue<int>(),QuoteToken=review["quoteToken"]!.ToString()},default);
 Check(submitted["status"]!.ToString()=="PENDING_APPROVAL"&&submitted["total"]==null,flow+" submission waits for Dispatcher without a zero-fee bill");
 var order=await orders.GetOrder(id,default);
 await MustReject(async()=>{await orders.CreateServicePayment(id,new(){RequestId=Guid.NewGuid(),ExpectedVersion=order["version"]!.GetValue<int>()},default);},"Payment is blocked before receiving Dispatcher result");
 var reply=new ServiceBillingResultInput{RequestId=Guid.NewGuid(),ExpectedVersion=order["version"]!.GetValue<int>(),Reference="OTHER-TEAM-"+flow,Total=345678,SourceDocumentsChecked=true,SourceEvidenceReference="DO-REVIEW-REFERENCE"};
 await MustReject(async()=>{await orders.ReceiveBillingResult(id,reply,default);},"Customer cannot set the payable amount");
 await MustReject(async()=>{await dispatcher.ReceiveBillingResult(id,reply with{Total=-1},default);},"Invalid returned amount is rejected");
 await dispatcher.ReceiveBillingResult(id,reply,default);await dispatcher.ReceiveBillingResult(id,reply,default);
 order=await orders.GetOrder(id,default);
 Check(order["status"]!.ToString()=="APPROVED"&&order["total"]!.GetValue<decimal>()==345678&&order["fees"]!.AsArray().Count==0,"Uses the amount returned by the other module without calculating prices");
 await MustReject(async()=>{await dispatcher.ReceiveBillingResult(id,reply with{Total=999},default);},"Same request cannot overwrite returned amount");
 mail.FailNext=true;Check(!await new ServiceMailQueue(db,mail).SendNext(default),"SMTP failure is recorded without undoing Dispatcher result");
 Check((await orders.GetOrder(id,default))["status"]!.ToString()=="APPROVED","Email failure does not change approval or payable amount");
 await DraftFixtures.Sql(pg,"UPDATE eport_service_mail_queue SET available_at=now() WHERE draft_id=@a AND kind='PAYMENT_NOTICE'",id);
 Check(await new ServiceMailQueue(db,mail).SendNext(default),"Payment notice email can retry from durable queue");
 Check(mail.Messages.Last().Name.StartsWith("Thong-bao-phi-"),"Notice includes printable form attachment");
 var payment=J(await orders.CreateServicePayment(id,new(){RequestId=Guid.NewGuid(),ExpectedVersion=order["version"]!.GetValue<int>()},default));
 Check(gateway.LastAmount==345678,"PayOS receives exactly the returned amount");
 var paymentId=Guid.Parse(payment["payment"]!["id"]!.ToString());gateway.Paid=true;order=await orders.GetOrder(id,default);
 await orders.SyncServicePayment(id,paymentId,new(){RequestId=Guid.NewGuid(),ExpectedVersion=order["version"]!.GetValue<int>()},default);
 Check(await new ServiceMailQueue(db,mail).SendNext(default)&&mail.Messages.Last().Name.StartsWith("Bien-lai-"),"Verified payment enqueues receipt email");
 Check(!await new ServiceMailQueue(db,mail).SendNext(default),"Queue does not resend sent notice or receipt");
 var countBefore=mail.Messages.Count;await orders.HandlePayOsWebhook(new JsonObject(),default);await orders.HandlePayOsWebhook(new JsonObject(),default);
 Check(!await new ServiceMailQueue(db,mail).SendNext(default)&&mail.Messages.Count==countBefore,"Repeated verified payment callback does not enqueue another receipt");
 await DraftFixtures.Sql(pg,"UPDATE eport_service_email_verifications SET created_at=now()-interval '2 minutes'");gateway.Paid=false;gateway.Link=null;
}
Check(before==await DraftFixtures.Fingerprint(pg),"Submission/payment do not modify Booking or container sources");
Console.WriteLine($"{checks} dispatcher integration checks passed; no real email or payment.");
sealed class Customer(int n=1,string role="Transport Company"):ICurrentUser{public Guid? UserId=>DraftFixtures.Id(n);public string? Role=>role;public string? Username=>"fixture";public string? Email=>"fixture@example.invalid";public bool IsAuthenticated=>true;}
sealed class Mail:IServiceWorkflowEmail{public string Code="";public bool FailNext;public List<(string Name,string Html)> Messages=[];public Task SendCode(string email,string code,CancellationToken ct){Code=code;return Task.CompletedTask;}public Task SendMessage(string email,string subject,string body,string name,string html,CancellationToken ct){if(FailNext){FailNext=false;throw new IOException("Fixture SMTP failure");}Messages.Add((name,html));return Task.CompletedTask;}}
sealed class Gateway:IPayOsGateway{public bool Configured=>true;public long LastAmount;public bool Paid;public PayOsPaymentLink? Link;public Task<PayOsPaymentLink> CreateAsync(long code,long amount,Guid id,CancellationToken ct){LastAmount=amount;Link=new(code,amount,"fixture-"+id.ToString("N"),"PENDING",0,amount,"https://pay.payos.vn/web/fixture");return Task.FromResult(Link);}public Task<PayOsPaymentLink?> GetAsync(long code,CancellationToken ct)=>Task.FromResult(Link==null?null:Paid?Link with{Status="PAID",AmountPaid=Link.Amount,AmountRemaining=0}:Link);public Task<PayOsPaymentLink> CancelAsync(long code,string reason,CancellationToken ct)=>throw new NotSupportedException();public PayOsWebhookData VerifyWebhook(JsonObject data)=>new(Link!.OrderCode,Link.Amount,Link.PaymentLinkId,"FIXTURE-REF-"+Link.PaymentLinkId,"VND","00",new JsonObject());}

sealed class Fleet:IBookingFleetAvailability {
 public bool Busy;
 public Task<BookingFleetOptions> GetFleetAsync(Guid company,DateTime start,DateTime end,CancellationToken ct){
  if(company!=DraftFixtures.Id(101))throw new Exception("Unexpected company");
  if(Busy)return Task.FromResult(new BookingFleetOptions());
  return Task.FromResult(BookingFleetPolicy.Rank(Enumerable.Range(1,2).Select(n=>new AvailableTruckDto{Id=DraftFixtures.Id(9010+n),CarrierId=company,DriverId=DraftFixtures.Id(9020+n),PlateNumber=n==1?"15H12345":"15H67890",Status="active",VehicleType="ROAD_TRUCK",PhotoUrl="https://example.invalid/"+Guid.NewGuid()}),Enumerable.Range(1,2).Select(n=>new AvailableDriverDto{Id=DraftFixtures.Id(9020+n),CarrierId=company,FullName="Fixture driver "+n,Phone="0901234567",LicenseNumber="LICENSE-"+n,Status="active"})));
 }
}

using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceOrderService
{
    public async Task<JsonObject> ReceiveBillingResult(Guid id,ServiceBillingResultInput input,CancellationToken ct)
    {
        Require(input.RequestId!=Guid.Empty&&input.ExpectedVersion>0,"Thiếu mã yêu cầu hoặc phiên bản hồ sơ.");
        var reference=(input.Reference??"").Trim();var reason=(input.Reason??"").Trim();var evidence=(input.SourceEvidenceReference??"").Trim();
        Require(reference.Length is >=3 and <=200&&!reference.Any(char.IsControl),"Thiếu mã kết quả từ Dispatcher.");
        Require(reason.Length<=1000&&evidence.Length<=200&&!reason.Any(c=>char.IsControl(c)&&c!='\n')&&!evidence.Any(char.IsControl),"Lý do hoặc căn cứ chứng từ không hợp lệ.");
        Require(input.Status is "APPROVED" or "REJECTED","Kết quả chỉ được duyệt hoặc từ chối.");
        Require(input.Status!="APPROVED"||input.Currency=="VND"&&input.Total.HasValue&&input.Total>=0&&input.Total<=9007199254740991m&&input.Total==decimal.Truncate(input.Total.Value),"Số tiền Dispatcher trả về phải là số nguyên VND không âm.");
        Require(input.Status!="REJECTED"||reason.Length>=3,"Nhập lý do từ chối.");
        Require(input.Status!="REJECTED"||input.Total==null,"Kết quả từ chối không kèm số tiền.");
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{actor=Actor,input.Status,reference,total=input.Total.HasValue?(long?)input.Total.Value:null,input.Currency,reason,input.SourceDocumentsChecked,evidence}))));
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await LockOrder(id,ct);var order=await OrderRow(id,null,ct,true);
        var replay=(await Rows("SELECT actor_id AS \"actorId\",fingerprint FROM eport_service_order_events WHERE draft_id=@p0 AND request_id=@p1",ct,id,input.RequestId)).SingleOrDefault();
        if(replay!=null){Require(G(replay,"actorId")==Actor&&S(replay,"fingerprint")==fingerprint,"Mã yêu cầu đã dùng cho kết quả khác.",409,"SERVICE_REQUEST_REUSED");await tx.CommitAsync(ct);return OrderView(order,null);}
        Require(order["version"]!.GetValue<int>()==input.ExpectedVersion,"Hồ sơ đã thay đổi; đọc lại phiên bản trước khi trả kết quả.",409,"SERVICE_ORDER_VERSION_CONFLICT");
        Require(S(order,"pricingMode")=="DISPATCHER"&&S(order,"pricingStatus")=="AWAITING"&&S(order,"status") is "CONFIRMED" or "PENDING_APPROVAL" or "RECEIVED","Hồ sơ không còn chờ kết quả Dispatcher.",409,"SERVICE_BILL_STATE");
        Require(input.Status!="APPROVED"||S(order,"flow")!="IMPORT"||input.SourceDocumentsChecked&&evidence.Length>=3,"Kết quả duyệt hàng nhập cần xác nhận đã đối chiếu DO/eDO và căn cứ quyền nhận hàng.",422,"SERVICE_SOURCE_REVIEW_REQUIRED");
        if(input.Status=="APPROVED")
            await Exec("INSERT INTO eport_service_bills(id,draft_id,request_id,actor_id,external_reference,total,note,fingerprint) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)",ct,Guid.NewGuid(),id,input.RequestId,Actor,reference,input.Total!.Value,reason,fingerprint);
        await Exec("UPDATE eport_service_order_states SET status=@p1,approval_status=@p1,payment_status=@p2,version=version+1,updated_at=now() WHERE draft_id=@p0",ct,id,input.Status,input.Status=="APPROVED"&&input.Total==0?"NOT_REQUIRED":"UNPAID");
        await AddEvent(id,input.Status=="APPROVED"?"DISPATCHER_FEES_RECEIVED":"DISPATCHER_REJECTED",S(order,"status"),input.Status,reason,ct,input.RequestId,fingerprint,new JsonObject{["reference"]=reference,["total"]=input.Total,["sourceDocumentsChecked"]=input.SourceDocumentsChecked,["sourceEvidenceReference"]=evidence});
        var saved=await OrderRow(id,null,ct);
        if(input.Status=="APPROVED")await QueueServiceMail(saved,"PAYMENT_NOTICE",ct);
        await tx.CommitAsync(ct);return OrderView(saved,null);
    }
}

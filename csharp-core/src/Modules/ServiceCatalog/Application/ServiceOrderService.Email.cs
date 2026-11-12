using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text.Json.Nodes;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceOrderService
{
    private async Task QueueServiceMail(JsonObject order,string kind,CancellationToken ct)
    {
        if(S(order,"pricingMode")!="DISPATCHER")return;
        var id=G(order,"id");
        var recipient=(await Rows("SELECT COALESCE((SELECT recipient FROM eport_service_mail_queue WHERE draft_id=r.draft_id AND kind='PAYMENT_NOTICE'),u.email) AS email FROM eport_service_registrations r JOIN users u ON u.id=r.confirmed_by WHERE r.draft_id=@p0",ct,id)).Single();
        Require(kind=="PAYMENT_RECEIPT"||MailAddress.TryCreate(S(recipient,"email"),out _),"Tài khoản đăng ký chưa có email hợp lệ để nhận thông báo.",422,"SERVICE_ACCOUNT_EMAIL_REQUIRED");
        var paid=kind=="PAYMENT_RECEIPT";
        var proof = paid ? (await Rows("SELECT number,bank_reference AS reference,updated_at AS \"paidAt\" FROM eport_service_payments WHERE draft_id=@p0 AND status='SUCCEEDED'",ct,id)).SingleOrDefault() : null;
        var title=paid?"Biên lai thanh toán dịch vụ":"Thông báo phí dịch vụ";
        string E(string? text)=>WebUtility.HtmlEncode(text??"");
        var amount=(order["total"]?.GetValue<decimal>()??0).ToString("N0",CultureInfo.GetCultureInfo("vi-VN"));
        var rows=string.Join("",order["fees"]!.AsArray().Select(f=>"<tr><td>"+E(S(f,"containerNumber"))+"</td><td>"+E(S(f,"serviceName"))+"</td><td>"+E(f?["quantity"]?.ToString())+"</td><td>"+E(f?["unitPrice"]?.ToString())+"</td><td>"+E(f?["amount"]?.ToString())+"</td></tr>"));
        var containers=string.Join(", ",order["containers"]!.AsArray().Select(c=>E(S(c,"containerNumber"))));
        var html="<!doctype html><html lang=\"vi\"><meta charset=\"utf-8\"><title>"+title+"</title><body><h1>NEXUSPORT — "+title+"</h1><p>Mã đơn: "+E(S(order,"number"))+"</p><p>Doanh nghiệp: "+E(S(order,"companyName"))+"</p><p>Chứng từ: "+E(S(order,"sourceLabel"))+"</p><p>Container: "+containers+"</p><p>Dịch vụ: "+E(S(order,"mainServiceName"))+"</p><p>Số tiền Dispatcher xác nhận: <strong>"+amount+" VND</strong></p><p>"+(paid?"Đã xác minh thanh toán thành công qua PayOS. Mã giao dịch: "+E(S(proof,"reference")):"Đã nhận kết quả từ Dispatcher. Vui lòng mở đơn dịch vụ để thanh toán PayOS nếu có phát sinh tiền.")+"</p>"+(rows.Length>0?"<table border=\"1\" cellpadding=\"8\"><tr><th>Container</th><th>Dịch vụ</th><th>Số lượng</th><th>Đơn giá VND</th><th>Thành tiền VND</th></tr>"+rows+"</table>":"")+"<p>Chứng từ dịch vụ, không phải hóa đơn điện tử hoặc giấy phép qua cổng.</p></body></html>";
        var payload=new JsonObject{["subject"]="NexusPort — "+title+" — "+S(order,"number"),["body"]=title+" cho đơn "+S(order,"number")+". Tổng phí "+amount+" VND. "+(paid?"Đã nhận thanh toán qua PayOS.":"Mở mục Đơn dịch vụ để thanh toán. Đơn không phát sinh tiền thì không cần tạo giao dịch.")+" Biểu mẫu chi tiết được đính kèm; có thể mở bằng trình duyệt để in.",["attachmentName"]=(paid?"Bien-lai-":"Thong-bao-phi-")+S(order,"number")+".html",["attachmentHtml"]=html};
        await Exec("INSERT INTO eport_service_mail_queue(id,draft_id,kind,recipient,payload) VALUES(@p0,@p1,@p2,@p3,CAST(@p4 AS jsonb)) ON CONFLICT(draft_id,kind) DO NOTHING",ct,Guid.NewGuid(),id,kind,S(recipient,"email"),payload.ToJsonString());
    }
}

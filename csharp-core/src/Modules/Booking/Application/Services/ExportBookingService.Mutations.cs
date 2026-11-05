using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;

namespace NexusPort.Modules.Booking.Application.Services;

public sealed partial class ExportBookingService
{
    public async Task<JsonObject> Decision(Guid id,ExportVersionRequest request,bool approve,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var b=await Booking(id,true,ct);OwnLine(b);Version(b,request.ExpectedVersion);
        Require(S(b,"status")=="Pending","Status","Chỉ yêu cầu đang chờ duyệt mới được xử lý.");
        var v=await Voyage(G(b,"voyageId"),true,ct);
        Require(S(v,"status")=="Scheduled"&&D(v,"vgmCutoff")>DateTime.UtcNow,"Voyage","Chuyến đã đóng hoặc quá hạn khai container.");
        if(!approve)Text(request.Reason??"","Lý do",1000);
        var submitted=b["submission"]!=null;
        await Exec("UPDATE export_bookings SET status=@p0,port_status=@p1,version=version+1,updated_at=now() WHERE id=@p2",ct,
            approve?(submitted?"ContainersDeclared":"Approved"):"Rejected",approve?(submitted?"Accepted":"WaitingContainers"):"Pending",id);
        if(submitted && !approve)await Exec("UPDATE export_booking_containers SET active=false WHERE export_booking_id=@p0",ct,id);
        if(submitted && approve)await ForwardSubmission(b,ct);
        await History(id,G(b,"voyageId"),approve?"BookingApproved":"BookingRejected",approve?"Hãng tàu duyệt yêu cầu và gửi thông tin dự kiến sang cảng.":request.Reason!,ct);
        if(approve)await Deliver(id,G(b,"voyageId"),I(b,"version")+1,"BookingApproved",new{bookingId=id,quantity=I(b,"quantity"),containerType=S(b,"containerTypeCode")},ct);
        if(approve && submitted)await Deliver(id,G(b,"voyageId"),I(b,"version")+1,"ContainerListUpdated",await Containers(id,ct),ct);
        await tx.CommitAsync(ct);return await Detail(id,ct);
    }
    public static bool ValidIso6346(string number)
    {
        if(!Regex.IsMatch(number,"^[A-Z]{3}[UJZ][0-9]{7}$"))return false;
        var sum=0;
        for(var index=0;index<10;index++)
        {
            var ch=number[index];var value=char.IsDigit(ch)?ch-'0':10+(ch-'A');
            if(char.IsLetter(ch)){value=10;for(var c='A';c<ch;c++){value++;if(value%11==0)value++;}}
            sum+=value*(1<<index);
        }
        return (sum%11)%10==number[10]-'0';
    }
    public async Task<JsonObject> Declare(Guid id,DeclareExportContainersRequest request,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var b=await Booking(id,true,ct);await Own(b,ct);Version(b,request.ExpectedVersion);
        Require(b["submission"]==null,"Submission","Hồ sơ Booking hợp nhất cần được cập nhật và gửi hãng tàu duyệt lại; không thay riêng danh sách đã gửi.");
        var v=await Voyage(G(b,"voyageId"),true,ct);
        Require(new[]{"Approved","ContainersDeclared","GateIn"}.Contains(S(b,"status")),"Status","Booking cần được hãng tàu duyệt trước khi khai container.");
        Require(S(v,"status")=="Scheduled"&&DateTime.UtcNow<D(v,"vgmCutoff")&&DateTime.UtcNow<D(v,"cyCutoff"),"Cutoff","Đã quá hạn khai VGM/container hoặc chuyến đã chốt.");
        Require(S(b,"siStatus")!="Confirmed","SI","SI đã xác nhận. Cần hãng tàu yêu cầu sửa trước khi đổi danh sách container.");
        Require(request.Containers is {Count:>0}&&request.Containers.Count<=I(b,"quantity"),"Containers","Danh sách phải có container và không vượt số lượng được duyệt.");
        Require(request.Containers.All(x=>x!=null),"Containers","Danh sách container không được chứa dòng trống.");
        Require(request.Containers.Select(x=>x.ContainerId).Distinct().Count()==request.Containers.Count&&request.Containers.Select(x=>x.DeclarationId).Distinct().Count()==request.Containers.Count,"Containers","Không được chọn trùng container/bản khai.");
        foreach(var containerId in request.Containers.Select(x=>x.ContainerId).OrderBy(x=>x))
            await Exec("SELECT id FROM containers WHERE id=@p0 FOR UPDATE",ct,containerId);
        var old=await Containers(id,ct);var oldByContainer=old.ToDictionary(x=>G(x,"containerId"));
        var sourceRows=new List<JsonObject>();var errors=new List<string>();
        for(var index=0;index<request.Containers.Count;index++)
        {
            var input=request.Containers[index];var label=$"Dòng {index+1}";
            var sources=await Rows("""
                SELECT row_to_json(x)::text AS "Value" FROM (SELECT c.container_no AS "containerNumber",t.code AS "containerTypeCode",d.cargo_type::text AS "cargoType"
                FROM container_declarations d JOIN containers c ON c.id=d.container_id JOIN container_types t ON t.id=c.container_type_id
                WHERE d.id=@p0 AND d.container_id=@p1 AND d.carrier_id=@p2 AND d.load_status='full') x
                """,ct,input.DeclarationId,input.ContainerId,G(b,"carrierId"));
            if(sources.Count!=1){errors.Add($"{label}: bản khai không thuộc công ty hoặc không có hàng.");continue;}
            var source=sources[0];sourceRows.Add(source);
            if(!ValidIso6346(S(source,"containerNumber")))errors.Add($"{label} ({S(source,"containerNumber")}): số container không đúng ISO 6346/check digit.");
            if(!string.Equals(S(source,"containerTypeCode"),S(b,"containerTypeCode"),StringComparison.OrdinalIgnoreCase))errors.Add($"{label}: loại container không khớp loại được duyệt.");
            if(string.IsNullOrWhiteSpace(input.SealNumber)||input.SealNumber.Length>100)errors.Add($"{label}: bổ sung số seal hợp lệ.");
            if(input.VgmKg<=0||input.VgmKg>99999999)errors.Add($"{label}: VGM phải lớn hơn 0, tính bằng kg.");
            if(S(source,"cargoType")=="reefer"&&!input.TargetTemperature.HasValue)errors.Add($"{label}: thiếu nhiệt độ chỉ dẫn hàng lạnh.");
            if(new[]{"dangerous","dangerous_goods"}.Contains(S(source,"cargoType"))&&(string.IsNullOrWhiteSpace(input.UnNumber)||string.IsNullOrWhiteSpace(input.DgClass)))errors.Add($"{label}: thiếu mã UN/phân lớp hàng nguy hiểm.");
            if(input.UnNumber?.Length>30||input.DgClass?.Length>30)errors.Add($"{label}: thông tin UN/phân lớp quá dài.");
            if(!string.IsNullOrWhiteSpace(input.CustomsDocumentUrl)&&!(Uri.TryCreate(input.CustomsDocumentUrl,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http")&&!input.CustomsDocumentUrl.StartsWith("/uploads/documents/",StringComparison.Ordinal))errors.Add($"{label}: đường dẫn chứng từ hải quan không hợp lệ.");
            var reserved=await Rows("SELECT jsonb_build_object('id',id)::text AS \"Value\" FROM export_booking_containers WHERE container_id=@p0 AND active AND export_booking_id<>@p1",ct,input.ContainerId,id);
            if(reserved.Count>0)errors.Add($"{label}: container đã có Booking hãng tàu còn hiệu lực.");
            if(oldByContainer.TryGetValue(input.ContainerId,out var previous)&&(S(previous,"status")!="Expected"||previous["gateBookingId"]!=null))
            {
                if(G(previous,"declarationId")!=input.DeclarationId||S(previous,"sealNumber")!=input.SealNumber.Trim()||previous["vgmKg"]!.GetValue<decimal>()!=input.VgmKg||previous["targetTemperature"]?.GetValue<decimal?>()!=input.TargetTemperature||S(previous,"unNumber")!=(input.UnNumber??"")||S(previous,"dgClass")!=(input.DgClass??"")||S(previous,"customsDocumentUrl")!=(input.CustomsDocumentUrl??""))errors.Add($"{label}: container đã có lịch xe/đã vào cổng; không thể thay hồ sơ nguồn, seal, VGM hoặc chỉ dẫn.");
            }
        }
        foreach(var previous in old.Where(x=>S(x,"status")!="Expected"||x["gateBookingId"]!=null))
            if(!request.Containers.Any(x=>x.ContainerId==G(previous,"containerId")))errors.Add($"Không được bỏ container {S(previous,"containerNumber")} đã có lịch xe hoặc đã vào cảng.");
        foreach(var previous in old.Where(x=>x["hasGateHistory"]?.GetValue<bool>()==true))
            if(!request.Containers.Any(x=>x.ContainerId==G(previous,"containerId")))errors.Add($"Container {S(previous,"containerNumber")} đã có lịch sử điều xe; giữ hồ sơ này để bảo toàn lịch sử, liên hệ hãng tàu xử lý thay vỏ.");
        Require(errors.Count==0,"Containers",string.Join("\n",errors));
        var ids=request.Containers.Select(x=>x.ContainerId).ToArray();
        await Exec("DELETE FROM export_booking_containers WHERE export_booking_id=@p0 AND NOT(container_id=ANY(@p1))",ct,id,ids);
        for(var index=0;index<request.Containers.Count;index++)
        {
            var item=request.Containers[index];var source=sourceRows[index];
            if(oldByContainer.TryGetValue(item.ContainerId,out var previous))
            {
                var changed=G(previous,"declarationId")!=item.DeclarationId||S(previous,"sealNumber")!=item.SealNumber.Trim()||previous["vgmKg"]!.GetValue<decimal>()!=item.VgmKg||S(previous,"customsDocumentUrl")!=(item.CustomsDocumentUrl??"");
                await Exec("""
                    UPDATE export_booking_containers SET declaration_id=@p0,seal_number=@p1,vgm_kg=@p2,target_temperature=@p3,un_number=@p4,dg_class=@p5,customs_document_url=@p6,
                    customs_cleared=CASE WHEN @p7 THEN false ELSE customs_cleared END WHERE id=@p8
                    """,ct,item.DeclarationId,item.SealNumber.Trim(),item.VgmKg,item.TargetTemperature,item.UnNumber,item.DgClass,item.CustomsDocumentUrl,changed,G(previous,"id"));
            }
            else await Exec("""
                INSERT INTO export_booking_containers(id,export_booking_id,container_id,declaration_id,container_no,type_code,seal_number,vgm_kg,cargo_type,target_temperature,un_number,dg_class,customs_document_url)
                VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12)
                """,ct,Guid.NewGuid(),id,item.ContainerId,item.DeclarationId,S(source,"containerNumber"),S(source,"containerTypeCode"),item.SealNumber.Trim(),item.VgmKg,S(source,"cargoType"),item.TargetTemperature,item.UnNumber,item.DgClass,item.CustomsDocumentUrl);
        }
        await Exec("UPDATE export_bookings SET status=CASE WHEN status='GateIn' THEN status ELSE 'ContainersDeclared' END,port_status='Accepted',si_status=CASE WHEN si_status='RevisionRequested' THEN si_status WHEN si IS NULL THEN 'None' ELSE 'Submitted' END,draft=NULL,version=version+1,updated_at=now() WHERE id=@p0",ct,id);
        await Deliver(id,G(b,"voyageId"),I(b,"version")+1,"ContainerListUpdated",request.Containers,ct);
        await History(id,G(b,"voyageId"),"ContainerListUpdated","Danh sách container được kiểm tra và cảng tiếp nhận phiên bản mới.",ct);
        await tx.CommitAsync(ct);return await Detail(id,ct);
    }
    public async Task<JsonObject> SubmitSi(Guid id,ExportSiRequest request,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var b=await Booking(id,true,ct);await Own(b,ct);Version(b,request.ExpectedVersion);
        var v=await Voyage(G(b,"voyageId"),true,ct);
        Require(new[]{"Approved","ContainersDeclared","GateIn","Loaded"}.Contains(S(b,"status")),"Status","Booking chưa được duyệt hoặc không còn cho phép nộp SI.");
        Require(DateTime.UtcNow<D(v,"siCutoff")||S(b,"siStatus")=="RevisionRequested","SiCutoff","Đã quá SI cut-off. Cần hãng tàu yêu cầu sửa/nộp SI ngoại lệ kèm lý do.");
        Require(S(b,"siStatus")!="Confirmed","SI","SI đã xác nhận; cần hãng tàu yêu cầu sửa.");
        Text(request.Shipper,"Người gửi hàng",1000);Text(request.Consignee,"Người nhận hàng",1000);Text(request.CargoDescription,"Mô tả hàng",2000);
        Require(request.NotifyParty?.Length<=1000||request.NotifyParty==null,"NotifyParty","Bên thông báo tối đa 1000 ký tự.");
        Require(request.PackagesCount>0&&request.GrossWeightKg>0,"Cargo","Số kiện và khối lượng hàng SI phải lớn hơn 0.");
        Require(request.FreightTerms is "Prepaid" or "Collect" && request.BlType is "Original" or "TelexRelease","SI","Điều khoản cước/loại B/L không hợp lệ.");
        Text(request.DestinationPort,"Cảng đến",200);
        Require(string.Equals(request.DestinationPort.Trim(),S(v,"portOfDischarge"),StringComparison.OrdinalIgnoreCase),"DestinationPort","Cảng đến SI phải khớp Booking.");
        // Cargo gross weight is intentionally not equated with packed-container VGM (includes tare).
        var si=JsonSerializer.SerializeToNode(new {request.Shipper,request.Consignee,request.NotifyParty,request.CargoDescription,request.PackagesCount,request.GrossWeightKg,request.FreightTerms,request.BlType,request.DestinationPort},Json)!;
        await Exec("UPDATE export_bookings SET si=@p0::jsonb,si_status='Submitted',draft=NULL,freight_paid=CASE WHEN si->>'freightTerms' IS DISTINCT FROM @p1 THEN false ELSE freight_paid END,version=version+1,updated_at=now() WHERE id=@p2",ct,si.ToJsonString(),request.FreightTerms,id);
        await History(id,G(b,"voyageId"),"SiSubmitted","Khách hàng nộp Shipping Instruction.",ct);
        await tx.CommitAsync(ct);return await Detail(id,ct);
    }
    public async Task<JsonObject> Draft(Guid id,ExportVersionRequest request,bool reopen,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var b=await Booking(id,true,ct);OwnLine(b);Version(b,request.ExpectedVersion);
        Require(!new[]{"Pending","Rejected","Cancelled","BlIssued"}.Contains(S(b,"status")),"Status","Trạng thái Booking không cho phép xử lý SI/draft.");
        if(reopen)
        {
            Text(request.Reason??"","Lý do",1000);
            await Exec("UPDATE export_bookings SET si_status='RevisionRequested',draft=NULL,version=version+1,updated_at=now() WHERE id=@p0",ct,id);
        }
        else
        {
            Require(S(b,"siStatus")=="Submitted"&&b["si"]!=null,"SI","Cần SI đã nộp trước khi lập draft B/L.");
            var containers=(await Containers(id,ct)).Where(x=>!new[]{"Excluded","Rolled"}.Contains(S(x,"status"))).ToList();
            Require(containers.Count>0,"Containers","Cần danh sách container trước khi lập draft B/L.");
            Require(b["si"]?["grossWeightKg"]?.GetValue<decimal>()<=containers.Sum(x=>x["vgmKg"]!.GetValue<decimal>()),"GrossWeightKg","Khối lượng hàng SI không thể lớn hơn tổng VGM của danh sách container.");
            var draft=new JsonObject{["version"]=I(b,"draftVersion")+1,["si"]=b["si"]!.DeepClone(),["containers"]=new JsonArray(containers.Select(x=>(JsonNode)x).ToArray()),["preparedAt"]=DateTime.UtcNow};
            await Exec("UPDATE export_bookings SET draft=@p0::jsonb,draft_version=draft_version+1,si_status='Draft',version=version+1,updated_at=now() WHERE id=@p1",ct,draft.ToJsonString(),id);
        }
        await History(id,G(b,"voyageId"),reopen?"SiRevisionRequested":"DraftBlPrepared",reopen?request.Reason!:"Hãng tàu lập bản nháp B/L từ SI và danh sách container hiện tại.",ct);
        await tx.CommitAsync(ct);return await Detail(id,ct);
    }
    public async Task<JsonObject> ConfirmDraft(Guid id,ExportVersionRequest request,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var b=await Booking(id,true,ct);await Own(b,ct);Version(b,request.ExpectedVersion);
        Require(!new[]{"Pending","Rejected","Cancelled","BlIssued"}.Contains(S(b,"status")),"Status","Booking không còn cho phép xác nhận draft.");
        Require(S(b,"siStatus")=="Draft"&&b["draft"]!=null&&I(b,"draftVersion")==request.DraftVersion,"Draft","Bản nháp đã đổi hoặc chưa được hãng tàu lập. Tải lại và kiểm tra trước khi xác nhận.");
        await Exec("UPDATE export_bookings SET si_status='Confirmed',version=version+1,updated_at=now() WHERE id=@p0",ct,id);
        await History(id,G(b,"voyageId"),"DraftBlConfirmed","Khách hàng xác nhận đúng phiên bản draft B/L; SI được khóa.",ct);
        await tx.CommitAsync(ct);return await Detail(id,ct);
    }
    public async Task<JsonObject> Cancel(Guid id,ExportVersionRequest request,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var b=await Booking(id,true,ct);await Own(b,ct);Version(b,request.ExpectedVersion);
        Require(!new[]{"Cancelled","Rejected","GateIn","Loaded","BlIssued"}.Contains(S(b,"status")),"Status","Booking đã có container vào cảng hoặc đã đóng không thể hủy.");
        var containers=await Containers(id,ct);
        Require(containers.All(x=>S(x,"status")=="Expected"),"Containers","Không thể hủy khi đã có container vào cổng.");
        Require(containers.All(x=>x["gateBookingId"]==null),"GateBooking","Hủy các lịch xe liên quan trước khi hủy Booking hãng tàu.");
        await Exec("UPDATE export_bookings SET status='Cancelled',port_status='Cancelled',version=version+1,updated_at=now() WHERE id=@p0",ct,id);
        await Exec("UPDATE export_booking_containers SET active=false WHERE export_booking_id=@p0",ct,id);
        await Deliver(id,G(b,"voyageId"),I(b,"version")+1,"BookingCancelled",new{id,request.Reason},ct);
        await History(id,G(b,"voyageId"),"BookingCancelled",request.Reason??"Khách hàng hủy Booking trước khi container vào cổng.",ct);
        await tx.CommitAsync(ct);return await Detail(id,ct);
    }
}

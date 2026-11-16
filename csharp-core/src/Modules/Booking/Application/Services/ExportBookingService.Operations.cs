using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public sealed partial class ExportBookingService
{
    public async Task<JsonObject> Freight(Guid id,ExportVersionRequest request,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var b=await Booking(id,true,ct);OwnLine(b);Version(b,request.ExpectedVersion);
        Require(b["si"]!=null&&!new[]{"Pending","Cancelled","Rejected","BlIssued"}.Contains(S(b,"status")),"Status","Booking/SI không còn cho phép thay đổi thanh toán cước.");
        await Exec("UPDATE export_bookings SET freight_paid=@p0,version=version+1,updated_at=now() WHERE id=@p1",ct,request.IsPaid,id);
        await History(id,G(b,"voyageId"),"FreightPaymentRecorded",request.IsPaid?"Hãng tàu ghi nhận đã thanh toán cước vận tải biển.":"Hãng tàu điều chỉnh trạng thái cước chưa thanh toán.",ct);
        await tx.CommitAsync(ct);return await Detail(id,ct);
    }
    public async Task<JsonObject> IssueBl(Guid id,ExportVersionRequest request,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var b=await Booking(id,true,ct);OwnLine(b);Version(b,request.ExpectedVersion);
        var v=await Voyage(G(b,"voyageId"),true,ct);
        var retained=(await Containers(id,ct)).Where(x=>!new[]{"Excluded","Rolled"}.Contains(S(x,"status"))).ToList();
        Require(S(v,"status")=="Departed"&&S(b,"status")=="Loaded"&&retained.Count>0&&retained.All(x=>S(x,"status")=="Loaded"),"Loading","Chỉ phát hành B/L khi chuyến đã rời cầu và toàn bộ container giữ lại đã xếp tàu.");
        Require(S(b,"siStatus")=="Confirmed"&&b["draft"]!=null,"SI","Khách hàng chưa xác nhận draft B/L.");
        var confirmedContainers=b["draft"]?["containers"]?.AsArray().OfType<JsonObject>().ToList()??new();
        Require(confirmedContainers.Count==retained.Count&&retained.All(container=>confirmedContainers.Any(confirmed=>
            G(confirmed,"id")==G(container,"id")&&S(confirmed,"sealNumber")==S(container,"sealNumber")&&
            confirmed["vgmKg"]!.GetValue<decimal>()==container["vgmKg"]!.GetValue<decimal>())),
            "Draft","Danh sách container giữ lại đã thay đổi. Hãng tàu cần yêu cầu sửa SI và khách hàng xác nhận draft mới trước khi phát hành.");
        Require(S(b["si"],"freightTerms")!="Prepaid"||b["freightPaid"]!.GetValue<bool>(),"Freight","Cước trả trước chưa được hãng tàu xác nhận thanh toán.");
        var number="BL-"+Guid.NewGuid().ToString("N").ToUpperInvariant();
        // Preserve the exact confirmed draft, adding final on-board evidence rather than editing it.
        var issued=b["draft"]!.DeepClone().AsObject();issued["blNumber"]=number;issued["issuedAt"]=DateTime.UtcNow;
        issued["onBoardAt"]=retained.Select(x=>D(x,"loadedAt")).Max();
        issued["loadingEvidence"]=new JsonArray(retained.Select(x=>(JsonNode)x).ToArray());
        await Exec("UPDATE export_bookings SET status='BlIssued',bl_number=@p0,draft=@p1::jsonb,version=version+1,updated_at=now() WHERE id=@p2",ct,number,issued.ToJsonString(),id);
        await History(id,G(b,"voyageId"),"BlIssued","Hãng tàu phát hành B/L "+number+" từ bản nháp đã xác nhận.",ct);
        await tx.CommitAsync(ct);return await Detail(id,ct);
    }
    private static void EventShape(ExportPortEventRequest request)
    {
        Require(request.EventId!=Guid.Empty,"EventId","Thiếu mã sự kiện chống xử lý trùng.");
        Require(request.OccurredAt.Kind==DateTimeKind.Utc&&request.OccurredAt<=DateTime.UtcNow.AddMinutes(2),"OccurredAt","Sự kiện thực tế cần thời điểm UTC, không được nằm trong tương lai.");
        Require(request.Reason?.Length<=1000||request.Reason==null,"Reason","Lý do tối đa 1000 ký tự.");
    }
    private async Task<bool> EventReplay(ExportPortEventRequest request,Guid? bookingId,Guid voyageId,CancellationToken ct)
    {
        await Exec("SELECT pg_advisory_xact_lock(hashtext(@p0))",ct,"export-event:"+request.EventId);
        var rows=await Rows("SELECT jsonb_build_object('hash',payload_hash,'bookingId',export_booking_id,'voyageId',voyage_id)::text AS \"Value\" FROM export_inbox WHERE event_id=@p0",ct,request.EventId);
        if(rows.Count==0)return false;
        Require(S(rows[0],"hash")==Hash(request)&&S(rows[0],"bookingId")== (bookingId?.ToString()??"")&&S(rows[0],"voyageId")==voyageId.ToString(),"EventId","Mã sự kiện đã dùng với nội dung hoặc Booking/chuyến tàu khác.");return true;
    }
    public async Task<JsonObject> PortEvent(Guid id,ExportPortEventRequest request,CancellationToken ct)
    {
        Require(Port,"Role","Chỉ nhân viên cảng được ghi nhận sự kiện thực tế.");EventShape(request);
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var b=await Booking(id,true,ct);
        if(await EventReplay(request,id,G(b,"voyageId"),ct)){await tx.CommitAsync(ct);return await Detail(id,ct);}
        Require(request.OccurredAt>=D(b,"createdAt"),"OccurredAt","Thời điểm sự kiện không thể trước khi tạo Booking.");
        Require(!new[]{"Cancelled","Rejected","BlIssued"}.Contains(S(b,"status")),"Status","Booking đã đóng, không thể ghi nhận sự kiện.");
        var v=await Voyage(G(b,"voyageId"),true,ct);var containers=await Containers(id,ct);
        var c=containers.SingleOrDefault(x=>G(x,"id")==request.ExportContainerId);
        if(c==null)throw new NotFoundException("ExportContainer",request.ExportContainerId);
        if(request.EventType=="CustomsCleared")
        {
            Require(!string.IsNullOrWhiteSpace(S(c,"customsDocumentUrl")),"Customs","Chưa có hồ sơ hải quan để nhân viên đối chiếu kết quả thông quan.");
            Require(S(c,"status") is "Expected" or "GateIn","Status","Container không còn ở trạng thái nhận hồ sơ hải quan.");
            Text(request.Reason??"","Kết quả đối chiếu hải quan",1000);
            await Exec("UPDATE export_booking_containers SET customs_cleared=true WHERE id=@p0",ct,request.ExportContainerId);
        }
        else if(request.EventType=="Loaded")
        {
            Require(Admin||string.Equals(_user.Role,"Yard Operator",StringComparison.OrdinalIgnoreCase),"Role","Chỉ nhân viên xếp tàu được ghi nhận Loaded.");
            Require(S(v,"status")=="Loading"&&S(c,"status")=="GateIn"&&c["customsCleared"]!.GetValue<bool>(),"Status","Chỉ xếp container đã qua cổng, đã thông quan và thuộc danh sách được chốt.");
            Require(request.OccurredAt>=D(c,"gateInAt"),"OccurredAt","Thời điểm xếp tàu không thể trước Gate In.");
            var positions=await Rows("""
                SELECT jsonb_build_object('id',p.id)::text AS "Value" FROM export_gate_links l JOIN booking_containers bc ON bc.booking_id=l.gate_booking_id
                JOIN container_visits visit ON visit.id=bc.container_visit_id AND visit.container_id=bc.container_id
                JOIN container_positions p ON p.container_id=bc.container_id AND p.is_current AND p.placed_at>=@p2
                JOIN containers c ON c.id=bc.container_id WHERE l.export_container_id=@p0 AND bc.container_id=@p1 AND c.status::text='in_yard' LIMIT 1
                """,ct,request.ExportContainerId,G(c,"containerId"),D(c,"gateInAt"));
            Require(positions.Count>0,"Yard","Chưa có bằng chứng container đã hạ đúng lượt bãi. Gate In xe chưa đủ điều kiện ghi nhận xếp tàu.");
            await Exec("UPDATE export_booking_containers SET status='Loaded',loaded_at=@p0 WHERE id=@p1",ct,request.OccurredAt,request.ExportContainerId);
        }
        else if(request.EventType=="GateInRejected")
        {
            Require(S(c,"status")=="Expected","Status","Chỉ container dự kiến mới có thể bị từ chối tại cổng.");Text(request.Reason??"","Lý do từ chối",1000);
        }
        else throw new ValidationException("EventType","Sự kiện không hợp lệ. Gate In phải được ghi nhận qua giao dịch cổng thực tế.");
        await Exec("UPDATE export_bookings SET status=CASE WHEN EXISTS(SELECT 1 FROM export_booking_containers WHERE export_booking_id=@p0 AND status NOT IN ('Loaded','Excluded','Rolled')) THEN status ELSE 'Loaded' END,version=version+1,updated_at=now() WHERE id=@p0",ct,id);
        await Exec("INSERT INTO export_inbox(event_id,export_booking_id,voyage_id,version,event,payload_hash) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",ct,request.EventId,id,G(b,"voyageId"),I(b,"version")+1,request.EventType,Hash(request));
        await Exec("INSERT INTO export_outbox(id,export_booking_id,voyage_id,version,event,payload,delivered_at) VALUES(@p0,@p1,@p2,@p3,@p4,@p5::jsonb,now())",ct,request.EventId,id,G(b,"voyageId"),I(b,"version")+1,request.EventType,JsonSerializer.Serialize(request,Json));
        await History(id,G(b,"voyageId"),request.EventType,request.Reason??"Cảng ghi nhận container đã xếp lên tàu.",ct);
        await tx.CommitAsync(ct);return await Detail(id,ct);
    }
    private void VoyageLine(JsonObject voyage) => Require(Admin||(Line&&G(voyage,"shippingLineId")==Actor),"Role","Không có quyền xử lý chuyến tàu của hãng khác.");
    public async Task<JsonObject> ClosePreview(Guid id,CancellationToken ct)
    {
        var v=await Voyage(id,false,ct);VoyageLine(v);
        var rows=await Rows("""
            SELECT row_to_json(x)::text AS "Value" FROM (SELECT e.id,e.export_booking_id AS "exportBookingId",e.container_no AS "containerNumber",e.status,e.customs_cleared AS "customsCleared",e.vgm_kg AS "vgmKg",
            CASE WHEN e.status<>'GateIn' THEN 'Container chưa vào cảng' WHEN NOT e.customs_cleared THEN 'Chưa thông quan' WHEN e.vgm_kg<=0 THEN 'VGM không hợp lệ' ELSE NULL END AS reason
            FROM export_booking_containers e JOIN export_bookings b ON b.id=e.export_booking_id
            WHERE b.voyage_id=@p0 AND b.status NOT IN ('Cancelled','Rejected') AND e.active ORDER BY e.container_no) x
            """,ct,id);
        return new JsonObject{["voyage"]=v,["eligible"]=new JsonArray(rows.Where(x=>x["reason"]==null).Select(x=>(JsonNode)x).ToArray()),["excluded"]=new JsonArray(rows.Where(x=>x["reason"]!=null).Select(x=>(JsonNode)x).ToArray())};
    }
    public async Task<JsonObject> CloseVoyage(Guid id,ExportVersionRequest request,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var v=await Voyage(id,true,ct);VoyageLine(v);Version(v,request.ExpectedVersion);
        Require(S(v,"status")=="Scheduled"&&DateTime.UtcNow>=D(v,"cyCutoff"),"CyCutoff","Chỉ chốt chuyến đang lên lịch sau CY cut-off.");
        var preview=await ClosePreview(id,ct);
        foreach(var item in preview["excluded"]!.AsArray().OfType<JsonObject>())
        {
            await Exec("UPDATE export_booking_containers SET status='Excluded',active=false,exclusion_reason=@p0 WHERE id=@p1",ct,S(item,"reason"),G(item,"id"));
            await History(G(item,"exportBookingId"),id,"ContainerExcluded",S(item,"containerNumber")+": "+S(item,"reason"),ct);
        }
        await Exec("UPDATE export_voyages SET status='Closed',version=version+1 WHERE id=@p0",ct,id);
        await Exec("UPDATE export_source_reservations r SET active=false FROM export_bookings b WHERE b.id=r.export_booking_id AND b.voyage_id=@p0 AND NOT EXISTS(SELECT 1 FROM export_booking_containers e WHERE e.export_booking_id=b.id AND e.container_id=r.container_id AND e.active)",ct,id);
        var bookings=await Rows("SELECT jsonb_build_object('id',id,'version',version)::text AS \"Value\" FROM export_bookings WHERE voyage_id=@p0 AND status NOT IN ('Cancelled','Rejected') FOR UPDATE",ct,id);
        foreach(var b in bookings)
        {
            await Exec("""
                UPDATE export_bookings SET version=version+1,updated_at=now(),
                  draft=CASE WHEN EXISTS(SELECT 1 FROM export_booking_containers e WHERE e.export_booking_id=@p0 AND e.status='Excluded') THEN NULL ELSE draft END,
                  si_status=CASE WHEN si IS NOT NULL AND EXISTS(SELECT 1 FROM export_booking_containers e WHERE e.export_booking_id=@p0 AND e.status='Excluded') THEN 'Submitted' ELSE si_status END
                WHERE id=@p0
                """,ct,G(b,"id"));
            await Deliver(G(b,"id"),id,I(b,"version")+1,"VoyageListClosed",preview,ct);
            await History(G(b,"id"),id,"VoyageListClosed","Hãng tàu chốt danh sách container theo bằng chứng Gate In, VGM và thông quan.",ct);
        }
        await tx.CommitAsync(ct);return await ClosePreview(id,ct);
    }
    public async Task<JsonObject> VoyageEvent(Guid id,ExportPortEventRequest request,CancellationToken ct)
    {
        Require(Admin,"Role","Chỉ nhân viên cầu bến được ghi nhận tàu xếp hàng/rời cầu.");EventShape(request);
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var v=await Voyage(id,true,ct);
        if(await EventReplay(request,null,id,ct)){await tx.CommitAsync(ct);return await Voyage(id,false,ct);}
        if(request.EventType=="LoadingStarted")Require(S(v,"status")=="Closed","Status","Cần chốt danh sách trước khi bắt đầu xếp tàu.");
        else if(request.EventType=="VoyageDeparted")
        {
            Require(S(v,"status")=="Loading","Status","Chuyến chưa ở trạng thái đang xếp tàu.");
            var remaining=await Rows("SELECT jsonb_build_object('id',e.id)::text AS \"Value\" FROM export_booking_containers e JOIN export_bookings b ON b.id=e.export_booking_id WHERE b.voyage_id=@p0 AND e.status NOT IN ('Loaded','Excluded','Rolled') LIMIT 1",ct,id);
            Require(remaining.Count==0,"Containers","Còn container giữ lại chưa xếp tàu; cần hoàn tất hoặc xử lý rớt tàu trước khi rời cầu.");
            var lastLoading=await Rows("""
                SELECT jsonb_build_object('at',max(at))::text AS "Value" FROM (
                  SELECT e.loaded_at AS at FROM export_booking_containers e JOIN export_bookings b ON b.id=e.export_booking_id WHERE b.voyage_id=@p0
                  UNION ALL SELECT (payload->>'occurredAt')::timestamptz FROM export_outbox WHERE voyage_id=@p0 AND event='LoadingStarted'
                ) times
                """,ct,id);
            Require(lastLoading[0]["at"]==null||request.OccurredAt>=D(lastLoading[0],"at"),"OccurredAt","Giờ tàu rời cầu phải sau các tác nghiệp xếp hàng thực tế.");
        }
        else throw new ValidationException("EventType","Sự kiện chuyến tàu không hợp lệ.");
        await Exec("UPDATE export_voyages SET status=@p0,departed_at=CASE WHEN @p1 THEN @p2 ELSE departed_at END,version=version+1 WHERE id=@p3",ct,request.EventType=="LoadingStarted"?"Loading":"Departed",request.EventType=="VoyageDeparted",request.OccurredAt,id);
        if(request.EventType=="VoyageDeparted")await Exec("UPDATE export_booking_containers e SET active=false FROM export_bookings b WHERE b.id=e.export_booking_id AND b.voyage_id=@p0 AND e.status='Loaded'",ct,id);
        if(request.EventType=="VoyageDeparted")await Exec("UPDATE export_source_reservations r SET active=false FROM export_bookings b WHERE b.id=r.export_booking_id AND b.voyage_id=@p0",ct,id);
        await Exec("INSERT INTO export_inbox(event_id,voyage_id,version,event,payload_hash) VALUES(@p0,@p1,@p2,@p3,@p4)",ct,request.EventId,id,I(v,"version")+1,request.EventType,Hash(request));
        await Exec("INSERT INTO export_outbox(id,voyage_id,version,event,payload,delivered_at) VALUES(@p0,@p1,@p2,@p3,@p4::jsonb,now())",ct,request.EventId,id,I(v,"version")+1,request.EventType,JsonSerializer.Serialize(request,Json));
        await Exec("INSERT INTO export_history(id,voyage_id,event,actor_id,description) VALUES(@p0,@p1,@p2,@p3,@p4)",ct,Guid.NewGuid(),id,request.EventType,Actor,"Cảng ghi nhận "+request.EventType+" lúc "+request.OccurredAt.ToString("O"));
        await tx.CommitAsync(ct);return await Voyage(id,false,ct);
    }
    public async Task<JsonObject?> Manifest(Guid id,CancellationToken ct)
    {
        var v=await Voyage(id,false,ct);VoyageLine(v);
        return (await Rows("SELECT jsonb_build_object('voyageId',voyage_id,'snapshot',snapshot,'status',status,'version',version,'sentAt',sent_at)::text AS \"Value\" FROM export_manifests WHERE voyage_id=@p0",ct,id)).SingleOrDefault();
    }
    public async Task<JsonObject> BuildManifest(Guid id,ExportVersionRequest request,bool send,CancellationToken ct)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        var v=await Voyage(id,true,ct);VoyageLine(v);
        Require(S(v,"status")=="Departed","Voyage","Chỉ lập manifest xuất khẩu sau khi tàu rời cầu theo phạm vi mô phỏng của dự án.");
        var current=await Manifest(id,ct);
        if(send)
        {
            Require(current!=null&&S(current,"status")=="Draft","Manifest","Cần manifest nháp trước khi gửi.");Version(current!,request.ExpectedVersion);
            await Exec("UPDATE export_manifests SET status='Sent',sent_at=now(),version=version+1 WHERE voyage_id=@p0",ct,id);
        }
        else
        {
            Version(v,request.ExpectedVersion);Require(current==null,"Manifest","Manifest đã được lập; không ghi đè bản đã tạo.");
            var all=await Rows("SELECT jsonb_build_object('id',id,'status',status,'blNumber',bl_number,'bl',draft)::text AS \"Value\" FROM export_bookings b WHERE voyage_id=@p0 AND EXISTS(SELECT 1 FROM export_booking_containers e WHERE e.export_booking_id=b.id AND e.status='Loaded') ORDER BY booking_no",ct,id);
            Require(all.Count>0&&all.All(x=>S(x,"status")=="BlIssued"),"BillOfLading","Cần phát hành đầy đủ B/L của các Booking đã xếp trước khi tổng hợp manifest.");
            var snapshot=new JsonObject{["voyage"]=v.DeepClone(),["billsOfLading"]=new JsonArray(all.Select(x=>(JsonNode)x).ToArray()),["totalBills"]=all.Count,
                ["totalContainers"]=all.Sum(x=>x["bl"]?["containers"]?.AsArray().Count??0),["totalCargoWeightKg"]=all.Sum(x=>x["bl"]?["si"]?["grossWeightKg"]?.GetValue<decimal>()??0)};
            await Exec("INSERT INTO export_manifests(voyage_id,snapshot) VALUES(@p0,@p1::jsonb)",ct,id,snapshot.ToJsonString());
        }
        await tx.CommitAsync(ct);return (await Manifest(id,ct))!;
    }
}

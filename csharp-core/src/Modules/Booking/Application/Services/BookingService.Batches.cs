using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public partial class BookingService
{
    private static readonly JsonSerializerOptions BatchJson=new(){PropertyNameCaseInsensitive=true};
    private static T BatchCopy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,BatchJson),BatchJson)!;
    private static bool BatchOverlap(BookingBatchTripRequest a,BookingBatchTripRequest b)=>a.AppointmentStart<b.AppointmentEnd&&a.AppointmentEnd>b.AppointmentStart;

    private static void ValidateBatchShape(BookingBatchRequest request,bool allowPast=false,bool planning=false)
    {
        if(request.CarrierId==Guid.Empty)throw new ValidationException("CarrierId","Cần xác định công ty đặt lô.");
        if(request.Trips==null||request.Trips.Count is <2 or >50)throw new ValidationException("Trips","Mỗi lô gồm từ 2 đến 50 container, mỗi container một chuyến xe.");
        if(request.BookingType is not (BookingType.Dropoff or BookingType.Pickup))throw new ValidationException("BookingType","Lô phải cùng nghiệp vụ Giao hoặc Nhận.");
        if(request.Trips.Any(trip=>trip==null||trip.Selection==null||trip.Operation==null||trip.Selection.ContainerId==Guid.Empty)||
            request.Trips.Select(trip=>trip.Selection.ContainerId).Distinct().Count()!=request.Trips.Count)
            throw new ValidationException("Trips","Chọn các container khác nhau từ danh sách hợp lệ.");
        if(request.Description?.Length>1000||request.SharedDocuments==null||request.SharedDocuments.Count>30)
            throw new ValidationException("SharedDocuments","Tối đa 30 chứng từ chung và 1000 ký tự ghi chú.");
        foreach(var trip in request.Trips)
        {
            BookingFleetPolicy.ValidateWindow(trip.AppointmentStart,trip.AppointmentEnd);
            if(!allowPast&&trip.AppointmentStart<DateTime.UtcNow.AddMinutes(-10))throw new ValidationException("Appointment","Chọn giờ hẹn trong tương lai cho từng chuyến.");
            if(trip.TrailerNumber?.Length>50)throw new ValidationException("TrailerNumber","Biển số rơ-moóc tối đa 50 ký tự.");
        }
        // Planning replaces draft assignments and must not be blocked by unfinished documents
        // or previously selected trailers. Creation still validates all of these fields.
        if(planning)return;
        foreach(var document in request.SharedDocuments)
        {
            if(document==null||string.IsNullOrWhiteSpace(document.DocumentType)||(string.IsNullOrWhiteSpace(document.FileUrl)&&string.IsNullOrWhiteSpace(document.DocumentNumber)))
                throw new ValidationException("SharedDocuments","Chứng từ chung cần loại và số chứng từ hoặc tệp đính kèm.");
            if(!string.IsNullOrWhiteSpace(document.FileUrl)&&
                !(Uri.TryCreate(document.FileUrl,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http")&&
                !document.FileUrl.StartsWith("/uploads/documents/",StringComparison.Ordinal))
                throw new ValidationException("SharedDocuments","Đường dẫn chứng từ không hợp lệ.");
        }
        for(var i=0;i<request.Trips.Count;i++)for(var j=0;j<i;j++)
            if(BatchOverlap(request.Trips[i],request.Trips[j])&&!string.IsNullOrWhiteSpace(request.Trips[i].TrailerNumber)&&
                NormalizeTrailer(request.Trips[i].TrailerNumber)==NormalizeTrailer(request.Trips[j].TrailerNumber))
                throw new ValidationException("TrailerNumber","Một rơ-moóc không thể phục vụ hai chuyến trùng giờ.");
    }
    private static string NormalizeTrailer(string? plate)=>new((plate??"").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public async Task<IReadOnlyList<BookingCargoSource>> GetBatchContainerOptionsAsync(Guid carrierId,string bookingType,BookingPlanningLeg? anchor,CancellationToken token)
    {
        var rows=(await GetBookingSourcesAsync(carrierId,null,token,bookingType,100)).ToList();
        if(bookingType=="Pickup"&&rows.Count>0)
        {
            // Only classify stays already authorized by the source query. Private seal/weight
            // and the exact cargo snapshot still require resolution of that stay's pickup code.
            var classifications=await _context.Database.SqlQueryRaw<BatchSourceClassification>("""
                SELECT v.id AS "VisitId",d.cargo_type::text AS "CargoType",d.load_status AS "LoadStatus"
                FROM container_visits v JOIN container_declarations d ON d.id=v.declaration_id
                WHERE v.id=ANY({0})
                """,rows.Select(row=>row.ContainerVisitId!.Value).ToArray()).ToDictionaryAsync(row=>row.VisitId,token);
            foreach(var row in rows)
                if(classifications.TryGetValue(row.ContainerVisitId!.Value,out var classification))
                {row.CargoType=classification.CargoType;row.LoadStatus=classification.LoadStatus;}
        }
        rows=rows.Where(row=>row.LoadStatus=="full"&&!string.IsNullOrWhiteSpace(row.CargoType)).ToList();
        if(anchor!=null)
        {
            var source=await ResolveBookingSourceAsync(carrierId,new BookingPlanningRequest{ContainerId=anchor.ContainerId,
                DeclarationId=anchor.DeclarationId,YardCode=anchor.YardCode,BookingType=bookingType},token,forPlanning:true);
            rows=rows.Where(row=>row.CargoType==source.CargoType).ToList();
        }
        return rows;
    }

    private static void ValidateBatchDocumentCoverage(BookingBatchRequest request,IReadOnlyList<CreateBookingDto> journeys)
    {
        var expected=journeys.Select(journey=>journey.Operations.Single().ContainerNumber.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        if(request.SharedDocuments.Count==0)throw new ValidationException("SharedDocuments","Lô cần bộ chứng từ dùng chung có phạm vi container cụ thể.");
        foreach(var document in request.SharedDocuments)
        {
            var numbers=document.ContainerNumbers?.Select(number=>(number??"").Trim().ToUpperInvariant()).ToList();
            if(numbers==null||numbers.Count>500||numbers.Distinct(StringComparer.Ordinal).Count()!=numbers.Count||!expected.IsSubsetOf(numbers))
                throw new ValidationException("SharedDocuments",$"Chứng từ {document.DocumentType}: danh sách số container phải bao gồm toàn bộ container của lô, không thiếu hoặc trùng; tối đa 500 số.");
            if(string.IsNullOrWhiteSpace(document.FileUrl))
                throw new ValidationException("SharedDocuments",$"Chứng từ {document.DocumentType}: cần tệp gốc để đối chiếu phạm vi container.");
            if(document.ExpiryDate.HasValue&&document.ExpiryDate.Value.Date<DateTime.UtcNow.Date)
                throw new ValidationException("SharedDocuments",$"Chứng từ {document.DocumentType} đã hết hạn.");
            document.ContainerNumbers=numbers;
        }
    }

    private async Task EnsureBatchDocumentsVerifiedAsync(Guid bookingId,CancellationToken token)
    {
        var inBatch=await _context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM booking_batch_trips WHERE booking_id={0}",bookingId).SingleAsync(token);
        if(inBatch==0)return;
        var operations=await _context.Set<Domain.Entities.BookingOperation>().AsNoTracking().Include(operation=>operation.Documents)
            .Where(operation=>operation.BookingId==bookingId).ToListAsync(token);
        foreach(var operation in operations)
        {
            var required=_documentRequirementPolicy.GetRequiredDocuments(operation.OperationType,operation.CargoStatus,operation.CargoType).Where(spec=>spec.IsMandatory);
            foreach(var spec in required)
                if(!operation.Documents.Any(document=>document.DocumentType==spec.DocumentType&&document.VerificationStatus=="Verified"
                    &&document.VerifiedBy.HasValue&&document.VerifiedAt.HasValue&&!string.IsNullOrWhiteSpace(document.FileUrl)
                    &&(!document.ExpiryDate.HasValue||document.ExpiryDate.Value.Date>=DateTime.UtcNow.Date)))
                    throw new ValidationException("Documents",$"Container {operation.ContainerNumber}: chứng từ {spec.NameVi} cần được đối chiếu và xác minh trước khi duyệt chuyến trong lô.");
        }
        if(operations.Count==0)throw new ValidationException("Documents","Chuyến trong lô chưa có hồ sơ tác nghiệp để đối chiếu.");
    }

    public async Task<BookingBatchPlan> PlanBatchAsync(BookingBatchRequest request,CancellationToken token)
    {
        ValidateBatchShape(request,planning:true);
        var result=new BookingBatchPlan();
        var fleets=new Dictionary<(DateTime Start,DateTime End),BookingFleetOptions>();
        var trailers=new Dictionary<(DateTime Start,DateTime End),List<BookingBatchTrailerSuggestion>>();
        for(var i=0;i<request.Trips.Count;i++)
        {
            var trip=request.Trips[i];
            var input=new BookingPlanningRequest {ContainerId=trip.Selection.ContainerId,DeclarationId=trip.Selection.DeclarationId,
                YardCode=trip.Selection.YardCode,BookingType=request.BookingType.ToString(),AppointmentStart=trip.AppointmentStart,AppointmentEnd=trip.AppointmentEnd};
            BookingCargoSource source;
            string? sourceError=null;
            try {source=await ResolveBookingSourceAsync(request.CarrierId,input,token,forPlanning:true);}
            catch(ValidationException exception)
            {
                source=new BookingCargoSource{ContainerId=trip.Selection.ContainerId};
                sourceError=$"Chuyến {i+1}: {string.Join(" ",exception.Errors.Values.SelectMany(messages=>messages))}";
                result.ValidationMessages.Add(sourceError);
            }
            var window=(trip.AppointmentStart,trip.AppointmentEnd);
            if(!fleets.TryGetValue(window,out var fleet))
            {
                fleet=await LoadBookingFleetAsync(request.CarrierId,trip.AppointmentStart,trip.AppointmentEnd,null,token);
                fleets[window]=fleet;
                // A container has no trailer plate. Only suggest a real plate previously used
                // by this company's tractor, and recheck it against live and overlapping trips.
                trailers[window]=await _context.Database.SqlQueryRaw<BookingBatchTrailerSuggestion>("""
                    SELECT DISTINCT ON (COALESCE(h.truck_id,h.vehicle_id))
                      COALESCE(h.truck_id,h.vehicle_id) AS "TruckId",upper(trim(h.trailer_number)) AS "TrailerNumber"
                    FROM bookings h
                    WHERE h.carrier_id={0} AND COALESCE(h.truck_id,h.vehicle_id) IS NOT NULL
                      AND h.status::text NOT IN ('canceled','rejected','expired')
                      AND h.appointment_end<={1} AND length(trim(h.trailer_number))>0
                      AND NOT EXISTS(SELECT 1 FROM bookings b
                        WHERE regexp_replace(upper(b.trailer_number),'[^A-Z0-9]','','g')=regexp_replace(upper(h.trailer_number),'[^A-Z0-9]','','g')
                        AND ((b.status::text NOT IN ('canceled','rejected','expired','completed') AND b.appointment_start<{2} AND b.appointment_end>{1})
                          OR b.status::text='checked_in'
                          OR (b.status::text NOT IN ('canceled','rejected','expired','completed') AND b.trip_status IN ('Arrived','Processing','PartiallyCompleted'))
                          OR EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type::text='gate_in' AND g.status::text='completed'
                            AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=b.id AND x.gate_type::text='gate_out'
                              AND x.status::text='completed' AND x.checked_at>=g.checked_at))))
                    ORDER BY COALESCE(h.truck_id,h.vehicle_id),h.appointment_end DESC,h.created_at DESC,h.id
                    """,request.CarrierId,new DateTimeOffset(ScheduleUtc(trip.AppointmentStart)),new DateTimeOffset(ScheduleUtc(trip.AppointmentEnd))).ToListAsync(token);
            }
            var plan=new BookingPlan{Source=source,Sources=new(){source},Fleet=BatchCopy(fleet)};
            var cargo=source.CargoType;
            if(sourceError==null)
            {
                if(string.IsNullOrEmpty(result.CargoType))result.CargoType=cargo!;
                else if(result.CargoType!=cargo)result.ValidationMessages.Add($"Chuyến {i+1} ({source.ContainerNumber}): loại hàng khác với lô. Hãy chọn container cùng loại hàng hoặc tách lô.");
            }
            var conflict=sourceError==null?BlockingContainer(await ContainerReservationsAsync(request.CarrierId,input,token,source),trip.AppointmentStart,trip.AppointmentEnd):null;
            var reserved=result.Trips.Where((_,index)=>BatchOverlap(trip,request.Trips[index])).ToList();
            var ranked=BookingFleetPolicy.Rank(plan.Fleet.Trucks.Where(truck=>!reserved.Any(row=>row.TruckId==truck.Id)),
                plan.Fleet.Drivers.Where(driver=>!reserved.Any(row=>row.DriverId==driver.Id)));
            // Keep every database-available option so the user can change an earlier choice.
            // Recommendations reserve draft resources; the UI also excludes other selected trips.
            plan.Fleet.RecommendedTruckId=ranked.RecommendedTruckId;plan.Fleet.RecommendedDriverId=ranked.RecommendedDriverId;
            plan.Fleet.Message=ranked.Message;
            plan.Schedule.IsAvailable=sourceError==null&&conflict==null&&ranked.RecommendedTruckId.HasValue&&ranked.RecommendedDriverId.HasValue;
            plan.Schedule.Message=sourceError??(conflict==null?ranked.Message:ContainerConflictMessage(source,conflict));
            plan.Schedule.RequiresExistingBookingResolution=conflict?.IsStay==true&&!conflict.EndsAt.HasValue;
            plan.Schedule.ExistingBookingId=conflict?.BookingId;plan.Schedule.ExistingBookingCode=conflict?.BookingCode;
            // Alternatives from a single-trip planner have not reserved the other draft journeys.
            plan.Schedule.Alternatives.Clear();
            var suggestions=trailers[window].Where(row=>plan.Fleet.Trucks.Any(truck=>truck.Id==row.TruckId)).ToList();
            result.Trips.Add(new(){ContainerId=trip.Selection.ContainerId,Plan=plan,CargoAvailable=sourceError==null&&conflict==null,
                TruckId=ranked.RecommendedTruckId,DriverId=ranked.RecommendedDriverId,TrailerSuggestions=suggestions,
                TrailerNumber=suggestions.FirstOrDefault(row=>row.TruckId==ranked.RecommendedTruckId
                    &&!reserved.Any(other=>NormalizeTrailer(other.TrailerNumber)==NormalizeTrailer(row.TrailerNumber)))?.TrailerNumber});
        }
        return result;
    }

    public async Task<BookingBatchDto> CreateBatchAsync(BookingBatchRequest request,CancellationToken token)
    {
        // An unchanged retry returns the original batch even after its appointment has passed.
        ValidateBatchShape(request,allowPast:true);
        if(request.RequestId==Guid.Empty)throw new ValidationException("RequestId","Cần mã yêu cầu để chống tạo lô trùng khi gửi lại.");
        if(!request.SharedDocumentsConfirmed)throw new ValidationException("SharedDocumentsConfirmed","Xác nhận chứng từ chung áp dụng cho tất cả container trong lô.");
        if(request.Trips.Any(trip=>trip.TruckId.HasValue!=trip.DriverId.HasValue||trip.TruckId==Guid.Empty||trip.DriverId==Guid.Empty))
            throw new ValidationException("Fleet","Mỗi chuyến cần đủ cặp xe và tài xế, hoặc để trống cả hai chờ phân công.");
        if(!request.AllowUnassignedTrips&&request.Trips.Any(trip=>!trip.TruckId.HasValue||!trip.DriverId.HasValue))
            throw new ValidationException("Fleet","Phân công đủ xe/tài xế hoặc cho phép lưu chuyến chờ phân công.");
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request,BatchJson))));
        await using var transaction=await _context.Database.BeginTransactionAsync(token);
        await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}),hashtext({1}))",new object[]{request.CarrierId.ToString(),request.RequestId.ToString()},token);
        var previous=await _context.Database.SqlQueryRaw<BatchReplay>("SELECT id AS \"Id\",request_hash AS \"Hash\" FROM booking_batches WHERE carrier_id={0} AND request_id={1}",request.CarrierId,request.RequestId).SingleOrDefaultAsync(token);
        if(previous!=null)
        {
            if(previous.Hash!=hash)throw new ValidationException("RequestId","Mã yêu cầu đã dùng cho nội dung khác. Tải lại thông tin lô trước khi gửi tiếp.");
            await transaction.CommitAsync(token);
            return await GetBatchAsync(previous.Id,request.CarrierId,token);
        }
        ValidateBatchShape(request);
        foreach(var plate in request.Trips.Select(trip=>NormalizeTrailer(trip.TrailerNumber)).Where(plate=>plate.Length>0).Distinct().OrderBy(plate=>plate))
            await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('booking-batch-trailer'),hashtext({0}))",new object[]{plate},token);
        // All callers acquire truck, driver and container locks in a stable order.
        foreach(var id in request.Trips.Where(trip=>trip.TruckId.HasValue).Select(trip=>trip.TruckId!.Value).Distinct().OrderBy(id=>id))
            await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM trucks WHERE id={0} FOR UPDATE",id).ToListAsync(token);
        foreach(var id in request.Trips.Where(trip=>trip.DriverId.HasValue).Select(trip=>trip.DriverId!.Value).Distinct().OrderBy(id=>id))
            await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM drivers WHERE id={0} FOR UPDATE",id).ToListAsync(token);
        foreach(var id in request.Trips.Select(trip=>trip.Selection.ContainerId).OrderBy(id=>id))
            await _context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM containers WHERE id={0} FOR UPDATE",id).ToListAsync(token);
        string? cargo=null;
        var journeys=new List<CreateBookingDto>();
        foreach(var trip in request.Trips)
        {
            var operation=BatchCopy(trip.Operation);
            operation.OperationType=request.BookingType==BookingType.Dropoff?"Export":"Import";
            operation.ContainerId=trip.Selection.ContainerId;operation.SequenceNumber=1;
            operation.ExpectedGateDirection=request.BookingType==BookingType.Dropoff?"GateIn":"GateOut";
            operation.Documents=request.SharedDocuments.Select(document=>BatchCopy<CreateBookingDocumentDto>(document)).Concat(operation.Documents??new()).ToList();
            // Clear foreign operation links: every shared reference is linked to this new journey's operation.
            foreach(var document in operation.Documents)document.BookingOperationId=null;
            var dto=new CreateBookingDto{CarrierId=request.CarrierId,BookingType=request.BookingType,TruckId=trip.TruckId,DriverId=trip.DriverId,
                TrailerNumber=trip.TrailerNumber,AppointmentStart=trip.AppointmentStart,AppointmentEnd=trip.AppointmentEnd,
                Description=request.Description,ContainerSelections=new(){BatchCopy(trip.Selection)},Operations=new(){operation},
                IsReeferPowerRequired=trip.IsReeferPowerRequired,TempTolerance=trip.TempTolerance};
            // Canonical cargo and per-container conditions are checked before any journey is stored.
            await PrepareBookingCargoAsync(dto,token);
            if(cargo!=null&&cargo!=operation.CargoType)throw new ValidationException("CargoType","Các container trong lô phải cùng loại hàng.");
            cargo=operation.CargoType;journeys.Add(dto);
        }
        ValidateBatchDocumentCoverage(request,journeys);
        var batchId=Guid.NewGuid();var code="LOT-"+Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO booking_batches(id,carrier_id,batch_code,request_id,request_hash,booking_type,cargo_type,description,shared_documents,created_by)
            VALUES({batchId},{request.CarrierId},{code},{request.RequestId},{hash},{request.BookingType.ToString().ToLowerInvariant()}::booking_type,
              {cargo},{request.Description},{JsonSerializer.Serialize(request.SharedDocuments,BatchJson)}::jsonb,{request.CreatedBy})
            """,token);
        try
        {
            for(var index=0;index<journeys.Count;index++)
            {
                var journey=journeys[index];journey.BookingCode=$"{code}-{index+1:D2}";
                if(!string.IsNullOrWhiteSpace(journey.TrailerNumber))
                {
                    var occupied=await _context.Database.SqlQueryRaw<int>("""
                        SELECT count(*)::int AS "Value" FROM bookings b
                        WHERE regexp_replace(upper(b.trailer_number),'[^A-Z0-9]','','g')={0}
                          AND b.status::text NOT IN ('canceled','rejected','expired')
                          AND ((b.status::text<>'completed' AND b.appointment_start<{2} AND b.appointment_end>{1}) OR
                            EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_in' AND g.status='completed'
                              AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=b.id AND x.gate_type='gate_out' AND x.status='completed')))
                        """,NormalizeTrailer(journey.TrailerNumber),new DateTimeOffset(journey.AppointmentStart),new DateTimeOffset(journey.AppointmentEnd)).SingleAsync(token);
                    if(occupied>0)throw new ValidationException("TrailerNumber","Rơ-moóc đã có lịch hoặc chuyến chưa ra cổng.");
                }
                var saved=await CreateJourneyAsync(journey,true,token);
                await _context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO booking_batch_trips(batch_id,booking_id,sequence_number) VALUES({batchId},{saved.Id},{index+1})",token);
            }
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);_context.ChangeTracker.Clear();throw;
        }
        return await GetBatchAsync(batchId,request.CarrierId,token);
    }

    private const string BatchHeaderSql="""
        SELECT id AS "Id",batch_code AS "BatchCode",booking_type::text AS "Direction",cargo_type AS "CargoType",
          description AS "Description",shared_documents::text AS "Documents",created_at AS "CreatedAt",
          created_by AS "CreatedBy",documents_confirmed_at AS "DocumentsConfirmedAt"
        FROM booking_batches
        """;
    public async Task<IReadOnlyList<BookingBatchDto>> GetBatchesAsync(Guid? carrierId,int page,CancellationToken token)
    {
        if(page is <1 or >10000)throw new ValidationException("Page","Trang không hợp lệ.");
        var headers=await _context.Database.SqlQueryRaw<BatchHeader>(BatchHeaderSql+" WHERE ({0}=false OR carrier_id={1}) ORDER BY created_at DESC,id LIMIT 20 OFFSET {2}",carrierId.HasValue,carrierId??Guid.Empty,(page-1)*20).ToListAsync(token);
        var result=new List<BookingBatchDto>();
        foreach(var header in headers)result.Add(await ReadBatchAsync(header,false,token));
        return result;
    }
    public async Task<BookingBatchDto> GetBatchAsync(Guid id,Guid? carrierId,CancellationToken token)
    {
        var header=await _context.Database.SqlQueryRaw<BatchHeader>(BatchHeaderSql+" WHERE id={0} AND ({1}=false OR carrier_id={2})",id,carrierId.HasValue,carrierId??Guid.Empty).SingleOrDefaultAsync(token);
        if(header==null)throw new NotFoundException("BookingBatch",id);
        return await ReadBatchAsync(header,true,token);
    }
    private async Task<BookingBatchDto> ReadBatchAsync(BatchHeader header,bool includeJourneys,CancellationToken token)
    {
        var rows=await _context.Database.SqlQueryRaw<BatchJourneyState>("""
            SELECT t.booking_id AS "Id",t.sequence_number AS "Sequence",b.status::text AS "Status",
              (b.driver_id IS NULL OR COALESCE(b.truck_id,b.vehicle_id) IS NULL) AS "Unassigned",
              EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_in' AND g.status='completed') AS "Entered",
              EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_out' AND g.status='completed') AS "Exited"
            FROM booking_batch_trips t JOIN bookings b ON b.id=t.booking_id WHERE t.batch_id={0} ORDER BY t.sequence_number
            """,header.Id).ToListAsync(token);
        var canceled=rows.Count(row=>row.Status is "canceled" or "rejected" or "expired");var completed=rows.Count(row=>row.Exited);
        var unassigned=rows.Count(row=>row.Unassigned&&!row.Exited&&row.Status is not ("canceled" or "rejected" or "expired"));
        var status=canceled==rows.Count?"Canceled":completed+canceled==rows.Count?"Completed":completed>0?"PartiallyCompleted":
            rows.Any(row=>row.Entered&&!row.Exited)?"InProgress":unassigned>0?"AwaitingAssignment":"Pending";
        var result=new BookingBatchDto{Id=header.Id,BatchCode=header.BatchCode,BookingType=Enum.Parse<BookingType>(header.Direction,true),CargoType=header.CargoType,
            Description=header.Description,CreatedAt=ScheduleUtc(header.CreatedAt),CreatedBy=header.CreatedBy,DocumentsConfirmedAt=ScheduleUtc(header.DocumentsConfirmedAt),SharedDocuments=JsonSerializer.Deserialize<List<BookingBatchDocumentDto>>(header.Documents,BatchJson)??new(),
            Status=status,TotalTrips=rows.Count,CompletedTrips=completed,CanceledTrips=canceled,UnassignedTrips=unassigned};
        if(includeJourneys)
        {
            var ids=rows.Select(row=>row.Id).ToList();
            var entities=await _context.Set<Domain.Entities.Booking>().AsNoTracking().Where(booking=>ids.Contains(booking.Id))
                .Include(booking=>booking.BookingContainers).Include(booking=>booking.BookingOperations).ThenInclude(op=>op.Documents).Include(booking=>booking.BookingDocuments).ToListAsync(token);
            var dtos=entities.Select(MapToDto).ToList();await EnrichBookingDtosAsync(dtos,token);
            result.Trips=rows.Select(row=>new BookingBatchTripDto{SequenceNumber=row.Sequence,Booking=dtos.Single(dto=>dto.Id==row.Id),GateInCompleted=row.Entered,GateOutCompleted=row.Exited}).ToList();
        }
        foreach(var document in result.SharedDocuments)
            if(!string.IsNullOrWhiteSpace(document.FileUrl))document.FileUrl=_fleetStorage.GetPresignedUrl(document.FileUrl);
        return result;
    }
}
public sealed class BatchReplay {public Guid Id{get;set;}public string Hash{get;set;}="";}
public sealed class BatchSourceClassification
{
    public Guid VisitId{get;set;}
    public string? CargoType{get;set;}
    public string LoadStatus{get;set;}="";
}
public sealed class BatchHeader
{
    public Guid Id{get;set;} public string BatchCode{get;set;}=""; public string Direction{get;set;}="";
    public string CargoType{get;set;}=""; public string? Description{get;set;} public string Documents{get;set;}="[]"; public DateTime CreatedAt{get;set;}
    public Guid? CreatedBy{get;set;} public DateTime DocumentsConfirmedAt{get;set;}
}
public sealed class BatchJourneyState
{
    public Guid Id{get;set;}public int Sequence{get;set;}public string Status{get;set;}="";
    public bool Unassigned{get;set;}public bool Entered{get;set;}public bool Exited{get;set;}
}

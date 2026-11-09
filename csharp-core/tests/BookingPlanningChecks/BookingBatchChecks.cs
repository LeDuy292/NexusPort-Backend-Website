using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Services;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Shared.Exceptions;

// Invoked only after CombinedJourneyChecks verifies the isolated server marker.
internal static class BookingBatchChecks
{
    public static async Task<int> RunAsync(AppDbContext context,BookingService service,Guid carrier,Guid type,DateTime start)
    {
        context.ChangeTracker.Clear();var count=0;
        void Check(bool value,string label){if(!value)throw new Exception("Batch: "+label);count++;}
        async Task Write(string sql,params object[] args)=>await context.Database.ExecuteSqlRawAsync(sql,args);
        async Task<T> Scalar<T>(string sql,params object[] args)=>await context.Database.SqlQueryRaw<T>(sql,args).SingleAsync();
        async Task<(Guid Container,Guid Declaration)> Source(string cargo="general")
        {
            var container=Guid.NewGuid();var declaration=Guid.NewGuid();
            await Write("""
                INSERT INTO containers(id,container_type_id,container_no,cargo_type,status) VALUES({0},{1},{2},'general','expected');
                INSERT INTO container_declarations(id,container_id,carrier_id,load_status,cargo_type,gross_weight_kg,seal_number)
                VALUES({3},{0},{4},'full',{5}::cargo_type,21000,'BATCH-SEAL');
                """,container,type,"BAT"+container.ToString("N")[..8],declaration,carrier,cargo);
            return(container,declaration);
        }
        var additionalDriver=Guid.NewGuid();var additionalTruck=Guid.NewGuid();
        await Write("""
            INSERT INTO drivers(id,carrier_id,full_name,license_number,status) VALUES({0},{1},'Batch driver','BATCH-LICENSE','active');
            INSERT INTO trucks(id,carrier_id,driver_id,plate_number,vehicle_type,status) VALUES({2},{1},{0},'BATCH-SECOND','ROAD_TRUCK','active');
            """,additionalDriver,carrier,additionalTruck);
        var a=await Source();var b=await Source();
        BookingBatchRequest Request(params (Guid Container,Guid Declaration)[] sources)=>new(){RequestId=Guid.NewGuid(),CarrierId=carrier,BookingType=BookingType.Dropoff,SharedDocumentsConfirmed=true,
            SharedDocuments=new(){new(){DocumentType="CarrierBookingReference",DocumentNumber="SHARED-NOTE",FileUrl="https://example.invalid/shared-note.pdf",ContainerNumbers=sources.Select(source=>("BAT"+source.Container.ToString("N")[..8]).ToUpperInvariant()).ToList()},new(){DocumentType="VgmCertificate",DocumentNumber="SHARED-VGM",FileUrl="https://example.invalid/shared-vgm.pdf",ContainerNumbers=sources.Select(source=>("BAT"+source.Container.ToString("N")[..8]).ToUpperInvariant()).ToList()}},
            Trips=sources.Select(source=>new BookingBatchTripRequest{Selection=new(){ContainerId=source.Container,DeclarationId=source.Declaration},AppointmentStart=start,AppointmentEnd=start.AddHours(2),Operation=new(){TemperatureSetpoint=0}}).ToList()};
        var request=Request(a,b);var plan=await service.PlanBatchAsync(request,default);
        Check(plan.Trips.Count==2&&plan.Trips.All(trip=>trip.CargoAvailable),"all selected cargo is resolved");
        Check(plan.Trips.All(trip=>trip.TruckId.HasValue&&trip.DriverId.HasValue)&&plan.Trips.Select(trip=>trip.TruckId).Distinct().Count()==2&&plan.Trips.Select(trip=>trip.DriverId).Distinct().Count()==2,"overlapping journeys receive different available trucks and drivers");
        Check(plan.Trips.All(trip=>trip.Plan.Fleet.Trucks.All(row=>row.Id!=Guid.Empty)&&trip.Plan.Fleet.Drivers.All(row=>row.Id!=Guid.Empty)),"dropdown resources retain real non-empty database IDs");
        Check(plan.Trips.All(trip=>trip.Plan.Fleet.Trucks.Any(row=>row.Id==plan.Trips[0].TruckId)),"manual dropdowns retain free resources even when draft recommendations reserve them");
        Check(plan.Trips.Single(trip=>trip.TruckId==additionalTruck).TrailerNumber==null,"a trailer plate is never invented for a tractor with no history");
        var histories=new List<Guid>();
        for(var i=0;i<2;i++)
        {
            var history=Guid.NewGuid();histories.Add(history);
            await Write("""
                INSERT INTO bookings(id,carrier_id,truck_id,booking_code,booking_type,status,appointment_start,appointment_end,
                  created_at,updated_at,is_combined,trip_status,trailer_number)
                VALUES({0},{1},{2},{3},'dropoff','completed',{4},{5},now(),now(),false,'Completed',{6})
                """,history,carrier,plan.Trips[i].TruckId!.Value,"HISTORY-"+history.ToString("N"),start.AddDays(-2),start.AddDays(-2).AddHours(1),"MOOC-"+i);
        }
        var withTrailers=await service.PlanBatchAsync(request,default);
        Check(withTrailers.Trips[0].TrailerNumber=="MOOC-0"&&withTrailers.Trips[1].TrailerNumber=="MOOC-1","available historical trailer plates follow the actual tractor");
        await Write("UPDATE bookings SET trailer_number='MOOC-0' WHERE id={0}",histories[1]);
        var duplicateTrailer=await service.PlanBatchAsync(request,default);
        Check(duplicateTrailer.Trips[0].TrailerNumber=="MOOC-0"&&duplicateTrailer.Trips[1].TrailerNumber==null,"overlapping draft journeys never receive the same historical trailer");
        await Write("UPDATE bookings SET status='pending',truck_id=NULL,appointment_start={0},appointment_end={1} WHERE id={2}",start,start.AddHours(2),histories[0]);
        var busyTrailer=await service.PlanBatchAsync(request,default);
        Check(busyTrailer.Trips.All(trip=>trip.TrailerSuggestions.All(row=>row.TrailerNumber!="MOOC-0")),"a trailer already reserved in the requested window is excluded");
        await Write("DELETE FROM bookings WHERE id={0} OR id={1}",histories[0],histories[1]);
        var sequential=Request(a,b);sequential.Trips[1].AppointmentStart=start.AddHours(2);sequential.Trips[1].AppointmentEnd=start.AddHours(4);
        var reusable=await service.PlanBatchAsync(sequential,default);
        Check(reusable.Trips.Select(trip=>trip.TruckId).Distinct().Count()==1&&reusable.Trips.Select(trip=>trip.DriverId).Distinct().Count()==1,"a fleet pair can be reused when journey windows do not overlap");
        for(var i=0;i<2;i++){request.Trips[i].TruckId=plan.Trips[i].TruckId;request.Trips[i].DriverId=plan.Trips[i].DriverId;}
        var batch=await service.CreateBatchAsync(request,default);
        Check(batch.TotalTrips==2&&batch.Trips.Count==2&&batch.Status=="Pending","one parent contains two real standard journeys");
        Check(batch.Trips.All(trip=>trip.Booking.ContainerIds.Count==1&&trip.Booking.Operations.Count==1),"gate granularity stays one vehicle journey per booking");
        Check(batch.Trips.Select(trip=>trip.Booking.DriverId).Distinct().Count()==2,"independent assignments persisted");
        Check(batch.Trips.All(trip=>trip.Booking.Operations.Single().Documents.Count==2),"each child operation is linked to the shared document references");
        Check(batch.Trips.SelectMany(trip=>trip.Booking.Operations.Single().Documents).Select(doc=>doc.FileUrl).Distinct().Count()==2,"two original file references shared, no duplicate upload required");
        Check(batch.Trips.SelectMany(trip=>trip.Booking.ContainerSelections).Select(selection=>selection.ContainerVisitId).Distinct().Count()==2,"containers retain separate yard occurrences");
        Check(batch.SharedDocuments.All(document=>document.ContainerNumbers.Count==2),"document coverage persists on the parent without extra database columns");
        Check(batch.Trips.SelectMany(trip=>trip.Booking.Operations.Single().Documents).All(document=>document.VerificationStatus!="Verified"),"upload and scope declaration never fabricate verified status");
        var review=typeof(BookingService).GetMethod("EnsureBatchDocumentsVerifiedAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        async Task Review(Guid bookingId)=>await (Task)review.Invoke(service,new object[]{bookingId,CancellationToken.None})!;
        try{await Review(batch.Trips[0].Booking.Id);throw new Exception("Unverified batch was approvable");}catch(ValidationException){count++;}
        var verifier=await Scalar<Guid>("SELECT id AS \"Value\" FROM users LIMIT 1");
        foreach(var document in batch.Trips[0].Booking.Operations.Single().Documents)
            await service.VerifyDocumentAsync(batch.Trips[0].Booking.Id,document.Id,new(){IsApproved=true},verifier);
        await Review(batch.Trips[0].Booking.Id);count++;
        try{await Review(batch.Trips[1].Booking.Id);throw new Exception("Sibling documents silently verified");}catch(ValidationException){count++;}
        var retry=await service.CreateBatchAsync(request,default);
        Check(retry.Id==batch.Id&&retry.Trips.Select(trip=>trip.Booking.Id).SequenceEqual(batch.Trips.Select(trip=>trip.Booking.Id)),"retry returns original batch and journeys");
        request.Description="changed request";
        try{await service.CreateBatchAsync(request,default);throw new Exception("Changed replay accepted");}catch(ValidationException){count++;}
        request.Description=null;
        var outsider=Guid.NewGuid();
        try{await service.GetBatchAsync(batch.Id,outsider,default);throw new Exception("Tenant leak");}catch(NotFoundException){count++;}
        Check(!(await service.GetBatchesAsync(outsider,1,default)).Any(row=>row.Id==batch.Id),"other tenant's batch list stays empty");
        Check((await service.GetBatchesAsync(null,1,default)).Any(row=>row.Id==batch.Id),"staff can read batch list without null SQL parameters");

        var first=batch.Trips[0].Booking.Id;var second=batch.Trips[1].Booking.Id;var gate=Guid.NewGuid();
        await Write("INSERT INTO gates(id,code,name,is_active) VALUES({0},'BATCH-GATE','Isolated gate',true); UPDATE bookings SET status='checked_in' WHERE id={1}; INSERT INTO gate_transactions(id,booking_id,gate_id,gate_type,status,checked_at) VALUES(gen_random_uuid(),{1},{0},'gate_in','completed',now()); UPDATE bookings SET status='completed' WHERE id={1}",gate,first);
        var progress=await service.GetBatchAsync(batch.Id,carrier,default);
        Check(progress.Status=="InProgress"&&progress.CompletedTrips==0,"yard handoff alone never completes a batch journey before physical exit");
        await Write("INSERT INTO gate_transactions(id,booking_id,gate_id,gate_type,status,checked_at,completed_at) VALUES(gen_random_uuid(),{0},{1},'gate_out','completed',now(),now())",first,gate);
        progress=await service.GetBatchAsync(batch.Id,carrier,default);
        Check(progress.Status=="PartiallyCompleted"&&progress.CompletedTrips==1&&!progress.Trips[1].GateOutCompleted,"one physical exit advances only its own journey");
        await Write("UPDATE bookings SET status='canceled' WHERE id={0}",second);
        Check((await service.GetBatchAsync(batch.Id,carrier,default)).Status=="Completed","one canceled sibling does not block other completed journeys forever");

        async Task RejectWithoutWrites(BookingBatchRequest invalid,string label)
        {
            var before=await Scalar<string>("SELECT concat((SELECT count(*) FROM bookings),':',(SELECT count(*) FROM booking_batches),':',(SELECT count(*) FROM container_visits),':',(SELECT count(*) FROM booking_documents)) AS \"Value\"");
            try{await service.CreateBatchAsync(invalid,default);throw new Exception("Invalid batch accepted: "+label);}
            catch(ValidationException){Check(before==await Scalar<string>("SELECT concat((SELECT count(*) FROM bookings),':',(SELECT count(*) FROM booking_batches),':',(SELECT count(*) FROM container_visits),':',(SELECT count(*) FROM booking_documents)) AS \"Value\""),label);}
        }
        var c=await Source();var d=await Source();var bad=Request(c,d);
        bad.Trips.ForEach(trip=>{trip.TruckId=plan.Trips[0].TruckId;trip.DriverId=plan.Trips[0].DriverId;});
        await RejectWithoutWrites(bad,"second journey fleet conflict rolls back first journey, batch, visits and documents");
        bad=Request(c,d);bad.AllowUnassignedTrips=true;bad.SharedDocuments.RemoveAt(1);
        await RejectWithoutWrites(bad,"mandatory shared document missing rejects the whole batch");
        bad=Request(c,d);bad.AllowUnassignedTrips=true;bad.SharedDocumentsConfirmed=false;
        await RejectWithoutWrites(bad,"scope confirmation required before sharing documents");
        var cold=await Source("reefer");bad=Request(c,cold);bad.AllowUnassignedTrips=true;
        var generalOptions=await service.GetBatchContainerOptionsAsync(carrier,"Dropoff",new BookingPlanningLeg{ContainerId=c.Container,DeclarationId=c.Declaration},default);
        Check(generalOptions.Any(row=>row.ContainerId==c.Container)&&generalOptions.Any(row=>row.ContainerId==d.Container)&&generalOptions.All(row=>row.CargoType=="general")&&!generalOptions.Any(row=>row.ContainerId==cold.Container),"anchor container filters the dropdown by canonical cargo rather than equipment size");
        Check(!(await service.GetBatchContainerOptionsAsync(outsider,"Dropoff",null,default)).Any(),"batch dropdown never exposes another carrier's declarations");
        var mixedPlan=await service.PlanBatchAsync(bad,default);
        Check(mixedPlan.ValidationMessages.Any(message=>message.Contains("Chuyến 2"))&&mixedPlan.Trips.All(trip=>trip.Plan.Fleet.Trucks.Count>0&&trip.Plan.Fleet.Drivers.Count>0),"mixed cargo reports its offending journey without hiding available fleet");
        await RejectWithoutWrites(bad,"mixed cargo cannot masquerade as one shared-document batch");
        var unresolved=Request(c,d);unresolved.Trips[0].Selection.DeclarationId=Guid.NewGuid();
        var unresolvedPlan=await service.PlanBatchAsync(unresolved,default);
        Check(unresolvedPlan.ValidationMessages.Count==1&&!unresolvedPlan.Trips[0].CargoAvailable&&unresolvedPlan.Trips[1].CargoAvailable&&unresolvedPlan.Trips.All(trip=>trip.Plan.Fleet.Trucks.Count>0),"one invalid source leaves sibling cargo and fleet selectable while reporting the specific journey");
        Check(unresolvedPlan.Trips[0].Plan.Source.ContainerNumber=="","invalid source cannot expose another declaration's cargo");
        bad=Request(c,c);bad.AllowUnassignedTrips=true;
        await RejectWithoutWrites(bad,"same physical container cannot appear twice");
        bad=Request(c,d);bad.AllowUnassignedTrips=true;bad.Trips[0].TrailerNumber="43R-123";bad.Trips[1].TrailerNumber="43r123";
        var correctedDraft=await service.PlanBatchAsync(bad,default);
        Check(correctedDraft.ValidationMessages.Count==0&&correctedDraft.Trips.All(trip=>trip.DriverId.HasValue),"optimization can replace duplicate stale trailer selections before create validation");
        await RejectWithoutWrites(bad,"same normalized trailer cannot serve overlapping journeys");
        bad=Request(c,d);bad.AllowUnassignedTrips=true;bad.SharedDocuments[0].ContainerNumbers.RemoveAt(1);
        await RejectWithoutWrites(bad,"checkbox confirmation cannot substitute for a missing container in document coverage");
        bad=Request(c,d);bad.AllowUnassignedTrips=true;bad.SharedDocuments[0].ContainerNumbers[1]="FAKE1234567";
        await RejectWithoutWrites(bad,"document scope containing a foreign container is rejected atomically");
        bad=Request(c,d);bad.AllowUnassignedTrips=true;bad.SharedDocuments[0].ContainerNumbers[1]=bad.SharedDocuments[0].ContainerNumbers[0];
        await RejectWithoutWrites(bad,"duplicate scope entries cannot pretend to cover both containers");
        bad=Request(c,d);bad.AllowUnassignedTrips=true;bad.SharedDocuments[0].FileUrl=null;
        await RejectWithoutWrites(bad,"a document number alone cannot prove the batch's shared-document scope");
        var waiting=Request(c,d);waiting.AllowUnassignedTrips=true;
        var partial=await service.CreateBatchAsync(waiting,default);
        Check(partial.Status=="AwaitingAssignment"&&partial.UnassignedTrips==2,"explicit partial assignment creates pending journeys, not invented drivers");
        Check(await Scalar<int>("SELECT count(*)::int AS \"Value\" FROM gate_entry_orders WHERE booking_id=ANY({0})",partial.Trips.Select(trip=>trip.Booking.Id).ToArray())==0,"unassigned journeys have no entry permission");
        var receive=Request(c,d);receive.BookingType=BookingType.Pickup;receive.AllowUnassignedTrips=true;
        receive.SharedDocuments=new(){new(){DocumentType="BillOfLading",DocumentNumber="SHARED-BL"},new(){DocumentType="DeliveryOrder",DocumentNumber="SHARED-DO"},new(){DocumentType="CustomsDeclaration",DocumentNumber="SHARED-CUSTOMS"}};
        foreach(var document in receive.SharedDocuments){document.FileUrl="https://example.invalid/"+document.DocumentType+".pdf";document.ContainerNumbers=new(){("BAT"+c.Container.ToString("N")[..8]).ToUpperInvariant(),("BAT"+d.Container.ToString("N")[..8]).ToUpperInvariant()};}
        for(var i=0;i<2;i++)
        {
            receive.Trips[i].Selection.DeclarationId=Guid.Empty;
            receive.Trips[i].Selection.YardCode=partial.Trips[i].Booking.ContainerSelections.Single().YardCode;
            receive.Trips[i].AppointmentStart=start.AddDays(1);receive.Trips[i].AppointmentEnd=start.AddDays(1).AddHours(2);
        }
        var pickupOptions=await service.GetBatchContainerOptionsAsync(carrier,"Pickup",new BookingPlanningLeg{ContainerId=c.Container,YardCode=receive.Trips[0].Selection.YardCode,BookingType="Pickup"},default);
        Check(pickupOptions.Any(row=>row.ContainerId==c.Container)&&pickupOptions.Any(row=>row.ContainerId==d.Container)&&pickupOptions.All(row=>row.CargoType=="general"&&row.LoadStatus=="full"),"authorized Pickup stays are classified and filtered before selecting a batch");
        var received=await service.CreateBatchAsync(receive,default);
        Check(received.BookingType==BookingType.Pickup&&received.Trips.All(trip=>trip.Booking.Operations.Single().OperationType=="Import"),"Pickup batches preserve the Import direction on each journey");
        Check(received.Trips.Select(trip=>trip.Booking.ContainerSelections.Single().ContainerVisitId).SequenceEqual(partial.Trips.Select(trip=>trip.Booking.ContainerSelections.Single().ContainerVisitId)),"Pickup uses the exact existing stays, never a new shared stay");
        var receiveFirst=received.Trips[0].Booking.Id;
        await Write("UPDATE bookings SET status='checked_in' WHERE id=ANY({0}); UPDATE bookings SET status='completed' WHERE id=ANY({0}); UPDATE bookings SET status='checked_in' WHERE id={1}; INSERT INTO gate_transactions(id,booking_id,gate_id,gate_type,status,checked_at) VALUES(gen_random_uuid(),{1},{2},'gate_in','completed',now()); UPDATE bookings SET status='completed' WHERE id={1}; INSERT INTO gate_transactions(id,booking_id,gate_id,gate_type,status,checked_at,completed_at) VALUES(gen_random_uuid(),{1},{2},'gate_out','completed',now(),now())",partial.Trips.Select(trip=>trip.Booking.Id).ToArray(),receiveFirst,gate);
        var receivedProgress=await service.GetBatchAsync(received.Id,carrier,default);
        Check(receivedProgress.Status=="PartiallyCompleted"&&receivedProgress.CompletedTrips==1,"one Pickup exit does not complete the other batch journey");
        Check(await Scalar<int>("SELECT count(*)::int AS \"Value\" FROM container_visits WHERE id=ANY({0}) AND visit_status='active'",partial.Trips.Select(trip=>trip.Booking.ContainerSelections.Single().ContainerVisitId!.Value).ToArray())==1,"only the outgoing journey's stay closes at its physical Gate Out");
        var extraA=await Source();var extraB=await Source();var subset=Request(extraA,extraB);subset.AllowUnassignedTrips=true;
        subset.SharedDocuments[0].ContainerNumbers.Add("MSCU1234567");
        foreach(var document in subset.SharedDocuments)document.DocumentNumber=null;
        var subsetSaved=await service.CreateBatchAsync(subset,default);
        Check(subsetSaved.TotalTrips==2&&subsetSaved.SharedDocuments[0].ContainerNumbers.Count==3,"a common document may cover additional containers beyond this batch as long as none of this batch are missing");
        Check(subsetSaved.Trips.SelectMany(trip=>trip.Booking.Operations.Single().Documents).All(document=>document.DocumentNumber==null),"original document files suffice without requiring typed or fabricated reference numbers");
        Console.WriteLine($"PASS {count} batch Booking planning, persistence, tenant, replay, rollback and independent gate checks.");
        return count;
    }
}

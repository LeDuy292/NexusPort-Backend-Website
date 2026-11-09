using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Services;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Modules.Booking.Infrastructure.Repositories;
using NexusPort.Modules.Driver.Domain.Enums;
using NexusPort.Modules.Vehicle.Domain.Enums;
using NexusPort.Modules.Container.Domain.Enums;

// Uses a schema-only copy loaded into a NEW, marked local database by the test runner.
// Never loads appsettings, application credentials, or deployed customer data.
internal static class CombinedJourneyChecks
{
    public static async Task RunAsync(string marker)
    {
        var builder=new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=55439;Database=booking_journey_check;Username=booking_check;Timeout=2");
        builder.MapEnum<DriverStatus>("driver_status");builder.MapEnum<TruckStatus>("truck_status");
        builder.MapEnum<BookingType>("booking_type");builder.MapEnum<BookingStatus>("booking_status");
        builder.MapEnum<ContainerStorageStatus>("container_status");
        await using var data=builder.Build();
        await using var connection=await data.OpenConnectionAsync();
        await using(var guard=new NpgsqlCommand("SELECT current_setting('nexusport.booking_test',true)",connection))
            if(marker.Length!=32||(string?)await guard.ExecuteScalarAsync()!=marker)throw new Exception("Unmarked database refused.");
        AppDbContext.ModuleAssemblies.Add(typeof(BookingRepository).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Driver.Infrastructure.Configurations.DriverConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Vehicle.Infrastructure.Configurations.VehicleConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Container.Infrastructure.Configurations.ContainerConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Yard.Infrastructure.Configurations.YardBlockConfiguration).Assembly);
        await using var context=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(data).Options);
        var policy=new BookingCompatibilityPolicy();var documents=new BookingDocumentRequirementPolicy();
        var service=new BookingService(new BookingRepository(context),new BookingValidationService(context,policy,documents),null!,context,
            NullLogger<BookingService>.Instance,policy,documents,new TestStorage());
        async Task Write(string sql,params object[] args)=>await context.Database.ExecuteSqlRawAsync(sql,args);
        async Task<T> Scalar<T>(string sql,params object[] args)=>await context.Database.SqlQueryRaw<T>(sql,args).SingleAsync();
        int checks=0;
        void Check(bool valid,string label){if(!valid)throw new Exception(label);checks++;}
        var carrier=Guid.NewGuid();var truck=Guid.NewGuid();var driver=Guid.NewGuid();var user=Guid.NewGuid();var type=Guid.NewGuid();
        var incoming=Guid.NewGuid();var outgoing=Guid.NewGuid();var dIn=Guid.NewGuid();var dOut=Guid.NewGuid();
        var start=DateTime.UtcNow.AddDays(2);var end=start.AddHours(2);
        await Write("""
            INSERT INTO carriers(id,company_name,status) VALUES({0},'Isolated journey test','active');
            INSERT INTO users(id,username,email,password,role,created_at,updated_at) VALUES({1},'isolated-driver','isolated@example.invalid','unused','Driver',now(),now());
            INSERT INTO drivers(id,carrier_id,user_id,full_name,license_number,status) VALUES({2},{0},{1},'Test driver','TEST-LICENSE','active');
            INSERT INTO trucks(id,carrier_id,driver_id,plate_number,vehicle_type,status) VALUES({3},{0},{2},'43C-TEST','ROAD_TRUCK','active');
            INSERT INTO container_types(id,code,size,category) VALUES({4},'40HC','ft40','dry');
            INSERT INTO containers(id,container_type_id,container_no,cargo_type,status) VALUES
              ({5},{4},'TEST0000001','general','in_yard'),({6},{4},'TEST0000002','general','in_yard');
            INSERT INTO container_cargo_types(code,name_vi,requires_power,sort_order) VALUES('general','General',false,1),('reefer','Reefer',true,2);
            INSERT INTO container_declarations(id,container_id,carrier_id,load_status,cargo_type,gross_weight_kg,seal_number) VALUES
              ({7},{5},{0},'full','general',21000,'SEAL-IN'),({8},{6},{0},'full','general',23000,'SEAL-OUT');
            """,carrier,user,driver,truck,type,incoming,outgoing,dIn,dOut);
        CreateBookingOperationDto Operation(Guid id,string kind,string cargo="General")=>new(){ContainerId=id,OperationType=kind,
            Documents=documents.GetRequiredDocuments(kind,"Loaded",cargo).Where(x=>x.IsMandatory).Select(x=>new CreateBookingDocumentDto{
                DocumentType=x.DocumentType,DocumentNumber="TEST-"+x.DocumentType,FileUrl="https://example.invalid/document",FileName="test.pdf"}).ToList()};
        var inbound=await service.CreateAsync(new CreateBookingDto{CarrierId=carrier,BookingType=BookingType.Dropoff,TruckId=truck,DriverId=driver,
            AppointmentStart=start.AddDays(-1),AppointmentEnd=end.AddDays(-1),ContainerSelections=new(){new(){ContainerId=outgoing,DeclarationId=dOut}},
            Operations=new(){Operation(outgoing,"Export")}});
        // Activate the already arrived container without making the selected fleet busy.
        await Write("UPDATE bookings SET status='checked_in' WHERE id={0}; UPDATE bookings SET status='completed' WHERE id={0}",inbound.Id);
        context.ChangeTracker.Clear();
        var code=await Scalar<string>("SELECT v.pickup_code AS \"Value\" FROM container_visits v JOIN booking_containers bc ON bc.container_visit_id=v.id WHERE bc.booking_id={0}",inbound.Id);
        var request=new BookingPlanningRequest{AppointmentStart=start,AppointmentEnd=end,Legs=new(){
            new(){ContainerId=incoming,DeclarationId=dIn,BookingType="Dropoff"},new(){ContainerId=outgoing,YardCode=code,BookingType="Pickup"}}};
        var plan=await service.PlanBookingAsync(carrier,request,default);
        Check(plan.Sources.Count==2&&plan.Sources[1].SealNumber=="SEAL-OUT","Both authorized cargo snapshots resolved.");
        Check(plan.Schedule.IsAvailable&&plan.Fleet.RecommendedTruckId==truck,"One shared available fleet pair planned.");
        var dto=new CreateBookingDto{CarrierId=carrier,IsCombined=true,DriverId=driver,TruckId=truck,TrailerNumber="TRAILER-TEST",
            AppointmentStart=start,AppointmentEnd=end,ContainerSelections=new(){new(){ContainerId=incoming,DeclarationId=dIn},new(){ContainerId=outgoing,YardCode=code}},
            Operations=new(){Operation(outgoing,"Import"),Operation(incoming,"Export")}};
        var saved=await service.CreateAsync(dto);
        Check(saved.IsCombined&&saved.Operations.Count==2&&saved.ContainerSelections.Count==2,"One combined booking persisted with both legs.");
        Check(saved.Operations.Single(op=>op.OperationType=="Export").SequenceNumber==1&&saved.Operations.Single(op=>op.OperationType=="Import").SequenceNumber==2,"Server normalizes order regardless of payload order.");
        Check(saved.Operations.All(op=>op.Documents.Count>0),"Each leg retains its own mandatory documents.");
        Check(saved.VehicleDetails!=null&&saved.DriverDetails!=null,"Shared fleet snapshots captured.");
        var stays=await Scalar<int>("SELECT count(DISTINCT container_visit_id)::int AS \"Value\" FROM booking_containers WHERE booking_id={0}",saved.Id);
        Check(stays==2,"Each leg bound to its exact distinct yard stay.");
        Check(await Scalar<string>("SELECT yard_booking_direction({0},{1}) AS \"Value\"",saved.Id,outgoing)=="pickup","Import direction derived from operation, not parent Dropoff.");
        var existingPlan=await service.PlanBookingAsync(carrier,new(){BookingId=saved.Id,ContainerId=incoming,BookingType="Dropoff",AppointmentStart=start,AppointmentEnd=end},default);
        Check(existingPlan.Sources.Count==2&&existingPlan.Schedule.IsAvailable,"Replanning an existing combined booking always checks both stored legs.");
        Check((await service.GetBookingSourcesAsync(carrier,null,default,"Pickup")).All(row=>row.ContainerId!=outgoing),"Reserved outgoing stay disappears from pickup dropdown.");
        await Write("UPDATE bookings SET status='checked_in' WHERE id={0}",saved.Id);
        Check(await Scalar<int>("SELECT count(*)::int AS \"Value\" FROM container_visits v JOIN booking_containers bc ON bc.container_visit_id=v.id WHERE bc.booking_id={0} AND v.visit_status='active'",saved.Id)==2,"Gate In activates only incoming stay and preserves outgoing active stay.");
        Check(await Scalar<int>("SELECT count(*)::int AS \"Value\" FROM eir_transactions WHERE booking_id={0} AND activity='in' AND eir_status='issued'",saved.Id)==1,"Only incoming leg issues incoming EIR.");
        var gate=Guid.NewGuid();
        await Write("""
            INSERT INTO gates(id,code,name,is_active) VALUES({0},'GATE-02','Test exit',true);
            INSERT INTO gate_transactions(id,booking_id,gate_id,gate_type,status,checked_at,completed_at)
              VALUES({1},{2},{0},'gate_in','completed',now()-interval '1 minute',now()-interval '1 minute');
            INSERT INTO yard_operation_events("Id",operation_id,container_id,driver_id,operation_status,event_type,delivery_status,occurred_at,created_at)
              VALUES(gen_random_uuid(),gen_random_uuid(),{3},{5},'Completed','Test','pending',now(),now()),
                    (gen_random_uuid(),gen_random_uuid(),{4},{5},'Completed','Test','pending',now(),now());
            """,gate,Guid.NewGuid(),saved.Id,incoming,outgoing,driver);
        context.ChangeTracker.Clear();
        var operations=await service.GetDriverOperationsAsync(driver);
        Check(operations.Single(op=>op.ContainerId==incoming).BookingType==BookingType.Dropoff&&operations.Single(op=>op.ContainerId==outgoing).BookingType==BookingType.Pickup,"Driver operations retain each container direction.");
        var first=await service.ConfirmContainerAsync(new(){BookingId=saved.Id,ContainerId=incoming,Condition="OK"},driver);
        Check(first.BookingStatus==BookingStatus.CheckedIn&&first.ContainerStatus=="in_yard","First handoff leaves combined trip in progress.");
        var second=await service.ConfirmContainerAsync(new(){BookingId=saved.Id,ContainerId=outgoing,Condition="OK"},driver);
        Check(second.BookingStatus==BookingStatus.Completed&&second.ContainerStatus=="moving","Both handoffs required for shared trip completion.");
        var again=await service.ConfirmContainerAsync(new(){BookingId=saved.Id,ContainerId=outgoing,Condition="OK"},driver);
        Check(again.ConfirmationId==second.ConfirmationId,"Repeated handoff is idempotent.");
        await Write("INSERT INTO gate_transactions(id,booking_id,gate_id,gate_type,status,checked_at,completed_at) VALUES({0},{1},{2},'gate_out','completed',now(),now())",Guid.NewGuid(),saved.Id,gate);
        Check(await Scalar<string>("SELECT status::text AS \"Value\" FROM containers WHERE id={0}",incoming)=="in_yard","Gate Out retains incoming container inside yard.");
        Check(await Scalar<string>("SELECT status::text AS \"Value\" FROM containers WHERE id={0}",outgoing)=="gate_out","Gate Out removes only outgoing container.");
        Check(await Scalar<int>("SELECT count(*)::int AS \"Value\" FROM container_visits v JOIN booking_containers bc ON bc.container_visit_id=v.id WHERE bc.booking_id={0} AND v.visit_status='completed'",saved.Id)==1,"Only pickup stay completed.");
        Check(await Scalar<int>("SELECT count(*)::int AS \"Value\" FROM eir_transactions WHERE booking_id={0} AND activity='out' AND eir_status='issued'",saved.Id)==1,"Outgoing EIR belongs to combined booking.");
        var countBefore=await Scalar<int>("SELECT count(*)::int AS \"Value\" FROM bookings");
        try
        {
            await service.CreateAsync(new CreateBookingDto{CarrierId=carrier,IsCombined=true,TruckId=truck,DriverId=driver,AppointmentStart=end.AddHours(1),AppointmentEnd=end.AddHours(2),
                ContainerSelections=new(){new(){ContainerId=incoming,DeclarationId=dIn},new(){ContainerId=incoming,YardCode=code}},
                Operations=new(){Operation(incoming,"Export"),Operation(incoming,"Import")}});
            throw new Exception("Same container accepted for both legs.");
        }
        catch(NexusPort.Shared.Exceptions.ValidationException){Check(await Scalar<int>("SELECT count(*)::int AS \"Value\" FROM bookings")==countBefore,"Invalid pair rolls back without partial bookings.");}
        foreach(var power in new[]{false,true})
        {
            context.ChangeTracker.Clear();
            var cold=Guid.NewGuid();var coldDeclaration=Guid.NewGuid();
            await Write("INSERT INTO containers(id,container_type_id,container_no,cargo_type,status) VALUES({0},{1},{2},'reefer','expected'); INSERT INTO container_declarations(id,container_id,carrier_id,load_status,cargo_type,gross_weight_kg,required_power_kw) VALUES({3},{0},{4},'full','reefer',19000,15)",cold,type,"COLD"+cold.ToString("N")[..7],coldDeclaration,carrier);
            var operation=Operation(cold,"Export","Reefer");operation.TemperatureSetpoint=0;
            var coldBooking=await service.CreateAsync(new CreateBookingDto{CarrierId=carrier,TruckId=truck,DriverId=driver,BookingType=BookingType.Dropoff,
                AppointmentStart=end.AddDays(power?2:1),AppointmentEnd=end.AddDays(power?2:1).AddHours(2),IsReeferPowerRequired=power,
                ContainerSelections=new(){new(){ContainerId=cold,DeclarationId=coldDeclaration}},Operations=new(){operation}});
            Check(coldBooking.IsReeferPowerRequired==power&&coldBooking.Operations.Single().TemperatureSetpoint==0,"Reefer power choice and zero Celsius survive persistence.");
            Check(await Scalar<int>("SELECT count(*)::int AS \"Value\" FROM reefer_power_requests WHERE booking_id={0}",coldBooking.Id)==(power?1:0),"Power request created only when explicitly requested.");
        }
        Console.WriteLine($"PASS {checks} combined Booking persistence and yard lifecycle checks on isolated PostgreSQL.");
        await BookingBatchChecks.RunAsync(context,service,carrier,type,start.AddDays(7));
    }
}

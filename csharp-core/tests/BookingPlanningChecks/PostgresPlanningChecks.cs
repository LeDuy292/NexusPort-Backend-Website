using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Services;
using NexusPort.Modules.Booking.Domain.Enums;
using NexusPort.Modules.Driver.Domain.Enums;
using NexusPort.Modules.Vehicle.Domain.Enums;

// Optional integration checks only target a separately started, marked local cluster.
// No connection strings or appsettings are loaded. The marker is checked before DDL.
internal static class PostgresPlanningChecks
{
    public static async Task RunAsync(string marker, string snapshotMigrationPath)
    {
        var builder = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=55439;Database=booking_check;Username=booking_check;Timeout=2");
        builder.MapEnum<DriverStatus>("driver_status"); builder.MapEnum<TruckStatus>("truck_status");
        builder.MapEnum<BookingType>("booking_type"); builder.MapEnum<BookingStatus>("booking_status");
        await using var dataSource = builder.Build();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var guard = new NpgsqlCommand("SELECT current_setting('nexusport.booking_test',true)", connection))
            if ((string?)await guard.ExecuteScalarAsync() != marker || marker.Length != 32)
                throw new InvalidOperationException("Refusing to touch an unmarked database.");
        await using (var schema = new NpgsqlCommand("""
            CREATE TYPE driver_status AS ENUM ('active','inactive','banned','waiting_confirmation','receiving_vehicle','vehicle_received','booking_confirmed','transporting','transport_completed');
            CREATE TYPE truck_status AS ENUM ('active','inactive','maintenance');
            CREATE TYPE booking_type AS ENUM ('pickup','dropoff');
            CREATE TYPE booking_status AS ENUM ('pending','ready','approved','rejected','canceled','checked_in','completed','expired');
            CREATE TABLE users(id uuid PRIMARY KEY);
            CREATE TABLE nexusport_schema_versions(version text PRIMARY KEY,description text);
            CREATE TABLE trucks(id uuid PRIMARY KEY,carrier_id uuid,driver_id uuid,plate_number text,vehicle_type text,status truck_status,
                rfid_tag text,photo_url text,registration_image_url text,current_location text,created_at timestamptz DEFAULT now());
            CREATE TABLE drivers(id uuid PRIMARY KEY,carrier_id uuid,user_id uuid,full_name text,phone text,id_card_number text,license_number text,
                photo_url text,id_card_front_url text,id_card_back_url text,license_image_url text,license_back_image_url text,
                id_card_expiry_date timestamptz,license_expiry_date timestamptz,status driver_status,created_at timestamptz DEFAULT now());
            CREATE TABLE container_types(id uuid PRIMARY KEY,code text);
            CREATE TABLE containers(id uuid PRIMARY KEY,container_no text,container_type_id uuid);
            CREATE TABLE container_declarations(id uuid PRIMARY KEY,container_id uuid,carrier_id uuid,load_status text,cargo_type text,
                gross_weight_kg numeric,required_power_kw numeric,customer_name text,seal_number text,created_at timestamptz DEFAULT now());
            CREATE TABLE container_visits(id uuid PRIMARY KEY,container_id uuid,declaration_id uuid,pickup_code text,visit_status text,started_at timestamptz,completed_at timestamptz);
            CREATE TABLE container_visit_access(container_visit_id uuid,carrier_id uuid);
            CREATE TABLE bookings(id uuid PRIMARY KEY,carrier_id uuid,driver_id uuid,truck_id uuid,vehicle_id uuid,vehicle_plate text,
                booking_type text,status text,trip_status text,appointment_start timestamptz,appointment_end timestamptz,booking_code text DEFAULT 'TEST-EXISTING');
            CREATE TABLE booking_containers(booking_id uuid,container_id uuid,declaration_id uuid,container_visit_id uuid);
            CREATE TABLE gate_transactions(id uuid,booking_id uuid,gate_type text,status text,checked_at timestamptz);
            CREATE TABLE transport_trips(id uuid,booking_id uuid,driver_id uuid,truck_id uuid,truck_plate text,status text,
                appointment_start timestamptz,appointment_end timestamptz);
            """, connection)) await schema.ExecuteNonQueryAsync();
        // Map enum OIDs after creating the local types.
        await connection.ReloadTypesAsync();
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Driver.Infrastructure.Configurations.DriverConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Vehicle.Infrastructure.Configurations.VehicleConfiguration).Assembly);
        AppDbContext.ModuleAssemblies.Add(typeof(NexusPort.Modules.Booking.Infrastructure.Configurations.BookingConfiguration).Assembly);
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(dataSource).Options);
        await BookingSaveChecks.EnsureSchemaAsync(context);
        var service = new BookingService(null!,null!,null!,context,NullLogger<BookingService>.Instance,
            new BookingCompatibilityPolicy(),new BookingDocumentRequirementPolicy(),new TestStorage());
        var carrier=Guid.NewGuid(); var otherCarrier=Guid.NewGuid(); var container=Guid.NewGuid(); var declaration=Guid.NewGuid();
        var driver=Guid.NewGuid(); var user=Guid.NewGuid(); var truck=Guid.NewGuid(); var start=DateTime.UtcNow.AddDays(2); var end=start.AddHours(2);
        async Task Write(string sql, params object[] values)
        {
            await using var command=new NpgsqlCommand(sql,connection);
            for(var i=0;i<values.Length;i++)command.Parameters.AddWithValue("p"+i,values[i]);
            await command.ExecuteNonQueryAsync();
        }
        var type=Guid.NewGuid();
        await Write("INSERT INTO container_types VALUES(@p0,'40HC'); INSERT INTO containers VALUES(@p1,'MSKU9082103',@p0)",type,container);
        await Write("INSERT INTO container_declarations(id,container_id,carrier_id,load_status,cargo_type,gross_weight_kg,seal_number) VALUES(@p0,@p1,@p2,'full','general',24000,'SEAL-SOURCE')",declaration,container,carrier);
        await Write("INSERT INTO drivers(id,carrier_id,user_id,full_name,license_number,status) VALUES(@p0,@p1,@p2,'Driver','LICENSE-REAL','active')",driver,carrier,user);
        var document="https://example.invalid/registration/truck.png";
        await Write("INSERT INTO users VALUES(@p0); INSERT INTO trucks(id,carrier_id,driver_id,plate_number,vehicle_type,status,registration_image_url) VALUES(@p1,@p2,@p3,'43C-12345','ROAD_TRUCK','active',@p4)",user,truck,carrier,driver,document);
        var request=new BookingPlanningRequest {ContainerId=container,DeclarationId=declaration,BookingType="Dropoff",AppointmentStart=start,AppointmentEnd=end};
        var count=0;
        void Check(bool value,string label){if(!value)throw new Exception("PostgreSQL check failed: "+label);count++;}
        var plan=await service.PlanBookingAsync(carrier,request,default);
        Check(plan.Source.SealNumber=="SEAL-SOURCE"&&plan.Source.GrossWeightKg==24000,"canonical cargo loaded");
        count+=await BookingScheduleChecks.RunAsync(context,service,carrier,container,declaration,truck,driver);
        Check(plan.Fleet.Trucks.Count==1&&plan.Fleet.Trucks[0].MaxPayloadTon==0&&plan.Fleet.RecommendedTruckId==truck,"missing capacity does not block availability or invent a payload");
        var migration=await File.ReadAllTextAsync(snapshotMigrationPath);
        await using(var command=new NpgsqlCommand(migration,connection)) {await command.ExecuteNonQueryAsync();await command.ExecuteNonQueryAsync();}
        plan=await service.PlanBookingAsync(carrier,request,default);
        Check(plan.Fleet.Trucks.Count==1,"snapshot migration preserves fleet availability");
        Check(plan.Fleet.RecommendedTruckId==truck&&plan.Fleet.RecommendedDriverId==driver,"eligible pair returned");
        Check((await service.GetBookingSourcesAsync(otherCarrier,"MSKU",default)).Count==0,"other tenant cannot list private declarations");
        Check((await service.GetBookingSourcesAsync(carrier,null,default)).Count==1,"dropdown loads own eligible declarations without manual typing");
        var busy=Guid.NewGuid();
        await Write("INSERT INTO bookings(id,carrier_id,driver_id,truck_id,vehicle_id,vehicle_plate,booking_type,status,trip_status,appointment_start,appointment_end) VALUES(@p0,@p1,@p2,@p3,NULL,NULL,'dropoff','pending','Scheduled',@p4,@p5)",busy,carrier,driver,truck,start,end);
        plan=await service.PlanBookingAsync(carrier,request,default);
        Check(plan.Fleet.Trucks.Count==0&&plan.Fleet.Drivers.Count==0,"overlapping pending booking excludes both");
        await Write("UPDATE bookings SET appointment_end=@p0 WHERE id=@p1",start,busy);
        Check((await service.PlanBookingAsync(carrier,request,default)).Fleet.Trucks.Count==1,"touching interval endpoint is not overlap");
        await Write("UPDATE bookings SET status='checked_in' WHERE id=@p0",busy);
        Check((await service.PlanBookingAsync(carrier,request,default)).Fleet.Trucks.Count==0,"running booking remains busy after appointment end");
        await Write("UPDATE bookings SET status='completed' WHERE id=@p0; INSERT INTO gate_transactions VALUES(@p1,@p0,'gate_in','completed',@p2)",busy,Guid.NewGuid(),start.AddDays(-1));
        Check((await service.PlanBookingAsync(carrier,request,default)).Fleet.Trucks.Count==0,"unclosed gate passage remains busy even if booking completed");
        await Write("INSERT INTO gate_transactions VALUES(@p0,@p1,'gate_out','completed',@p2)",Guid.NewGuid(),busy,start);
        Check((await service.PlanBookingAsync(carrier,request,default)).Fleet.Trucks.Count==1,"physical exit releases completed booking");
        await Write("INSERT INTO transport_trips VALUES(@p0,NULL,@p1,NULL,'43C12345','In Transit',@p2,@p3)",Guid.NewGuid(),user,start.AddDays(-1),start.AddHours(-1));
        plan=await service.PlanBookingAsync(carrier,request,default);
        Check(plan.Fleet.Trucks.Count==0&&plan.Fleet.Drivers.Count==0,"live transport trip maps user to driver and normalized plate to truck");
        await Write("UPDATE transport_trips SET status='Completed'");
        Check((await service.PlanBookingAsync(carrier,request,default)).Fleet.Trucks.Count==1,"unknown capacity does not block Booking");
        var ensureFleet=typeof(BookingService).GetMethod("EnsureBookingFleetAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        async Task EnsureAvailable() => await (Task)ensureFleet.Invoke(service,new object?[] {carrier,truck,driver,start,end,null,CancellationToken.None})!;
        await EnsureAvailable();
        Check(true,"save-time fleet validation accepts a truck without capacity");
        await Write("UPDATE container_declarations SET gross_weight_kg=NULL WHERE id=@p0",declaration);
        Check((await service.PlanBookingAsync(carrier,request,default)).Fleet.RecommendedTruckId==truck,"missing cargo weight cannot hide an available truck");
        await EnsureAvailable();
        await Write("UPDATE container_declarations SET gross_weight_kg=24000 WHERE id=@p0",declaration);
        await Write("UPDATE trucks SET registration_image_url='https://example.invalid/registration/replaced.png'");
        Check((await service.PlanBookingAsync(carrier,request,default)).Fleet.Trucks.Count==1,"changed registration does not block fleet selection");
        await Write("UPDATE trucks SET registration_image_url=@p0; UPDATE drivers SET status='transporting'",document);
        Check((await service.PlanBookingAsync(carrier,request,default)).Fleet.Drivers.Count==0,"busy driver status excluded without a booking row");
        await Write("UPDATE drivers SET status='active',license_expiry_date=@p0",start.AddDays(-1));
        Check((await service.PlanBookingAsync(carrier,request,default)).Fleet.Drivers.Count==0,"expired license excluded by database-backed planner");
        var visit=Guid.NewGuid(); var inbound=Guid.NewGuid();
        await Write("INSERT INTO container_visits(id,container_id,declaration_id,pickup_code,visit_status) VALUES(@p0,@p1,@p2,'TC-AAAA-BBBB-CCCC','active'); INSERT INTO bookings(id,carrier_id,driver_id,truck_id,vehicle_id,vehicle_plate,booking_type,status,trip_status,appointment_start,appointment_end) VALUES(@p3,@p4,NULL,NULL,NULL,NULL,'dropoff','completed','Completed',@p5,@p6); INSERT INTO booking_containers VALUES(@p3,@p1,@p2,@p0)",visit,container,declaration,inbound,carrier,start.AddDays(-2),start.AddDays(-1));
        Check((await service.GetBookingSourcesAsync(carrier,"MSKU",default)).Count==0,"booked declaration not offered as a new dropoff");
        var pickupOptions=await service.GetBookingSourcesAsync(carrier,null,default,"Pickup");
        Check(pickupOptions.Count==1&&pickupOptions[0].GrossWeightKg==null,"pickup dropdown lists identities without exposing inbound cargo");
        Check((await service.GetBookingSourcesAsync(otherCarrier,null,default,"Pickup")).Count==0,"other company cannot enumerate ungranted stays");
        await Write("INSERT INTO container_visit_access VALUES(@p0,@p1)",visit,otherCarrier);
        Check((await service.GetBookingSourcesAsync(otherCarrier,null,default,"Pickup")).Count==1,"explicit visit access allows dropdown selection");
        var pickup=new BookingPlanningRequest {ContainerId=container,YardCode="TC-AAAA-BBBB-CCCC",BookingType="Pickup",AppointmentStart=start,AppointmentEnd=end};
        Check((await service.PlanBookingAsync(otherCarrier,pickup,default)).Source.ContainerVisitId==visit,"valid capability resolves exact inbound stay for another authorized receiving company");
        pickup.YardCode="TC-FFFF-FFFF-FFFF";
        try { await service.PlanBookingAsync(otherCarrier,pickup,default); throw new Exception("Wrong pickup code accepted"); }
        catch(NexusPort.Shared.Exceptions.ValidationException) {count++;}
        pickup.YardCode="TC-AAAA-BBBB-CCCC";
        var outbound=Guid.NewGuid();
        await Write("INSERT INTO bookings(id,carrier_id,driver_id,truck_id,vehicle_id,vehicle_plate,booking_type,status,trip_status,appointment_start,appointment_end) VALUES(@p0,@p1,NULL,NULL,NULL,NULL,'pickup','pending','Scheduled',@p2,@p3); INSERT INTO booking_containers VALUES(@p0,@p4,@p5,@p6)",outbound,otherCarrier,start,end,container,declaration,visit);
        try { await service.PlanBookingAsync(otherCarrier,pickup,default); throw new Exception("Duplicate pickup offered"); }
        catch(NexusPort.Shared.Exceptions.ValidationException) {count++;}
        var vehicleService = new NexusPort.Modules.Vehicle.Application.Services.VehicleService(
            new NexusPort.Modules.Vehicle.Infrastructure.Repositories.VehicleRepository(context),null!,
            NullLogger<NexusPort.Modules.Vehicle.Application.Services.VehicleService>.Instance,new TestStorage());
        var create = new NexusPort.Modules.Vehicle.Application.DTOs.CreateVehicleDto {PlateNumber="TEST-30T",VehicleType="ROAD_TRUCK",RegistrationImageUrl=document};
        var created=await vehicleService.CreateAsync(carrier,create);
        Check(created.PlateNumber=="TEST-30T","vehicle creation works without payload fields");
        await Write("UPDATE drivers SET status='active',license_expiry_date=NULL; UPDATE trucks SET status='maintenance' WHERE id=@p0",truck);
        var newDeclaration=Guid.NewGuid();
        await Write("INSERT INTO container_declarations(id,container_id,carrier_id,load_status,cargo_type,gross_weight_kg) VALUES(@p0,@p1,@p2,'full','general',24000)",newDeclaration,container,carrier);
        request.DeclarationId=newDeclaration;
        var vehiclePlan=await service.PlanBookingAsync(carrier,request,default);
        Check(vehiclePlan.Fleet.RecommendedTruckId==created.Id&&vehiclePlan.Fleet.Trucks.Single().MaxPayloadTon==0,"Booking selects a free ROAD_TRUCK without loading its payload");
        await Write("ALTER TABLE bookings ADD COLUMN driver_name text; UPDATE trucks SET current_location='Gate 02',photo_url='fleet/original.png' WHERE id=@p0; UPDATE drivers SET phone='0900000001',id_card_number='TEST-ID',license_image_url='drivers/original-license.png' WHERE id=@p1",created.Id,driver);
        var fleet=await service.PlanBookingAsync(carrier,request,default);
        Check(fleet.Fleet.Trucks.Single().CurrentLocation=="Gate 02"&&fleet.Fleet.Drivers.Single().IdCardNumber=="TEST-ID","planner includes full selected vehicle and driver profile without payload");
        var snapshotBooking=new NexusPort.Modules.Booking.Domain.Entities.Booking(carrier,"TEST-SNAPSHOT",BookingType.Dropoff,start,end,driver,created.Id)
            {VehiclePlate="SPOOFED",DriverName="SPOOFED",TrailerNumber="TEST-TRAILER"};
        var capture=typeof(BookingService).GetMethod("CaptureFleetDetailsAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        async Task Capture() => await (Task)capture.Invoke(service,new object[] {snapshotBooking,CancellationToken.None})!;
        await Capture();
        Check(snapshotBooking.VehiclePlate=="TEST-30T"&&snapshotBooking.DriverName=="Driver","source profiles override client-supplied names and plates");
        Check(!snapshotBooking.VehicleSnapshot!.Contains("Payload")&&snapshotBooking.DriverSnapshot!.Contains("original-license.png"),"stored snapshot excludes payload and preserves original document keys");
        await Write("INSERT INTO bookings(id,carrier_id,driver_id,truck_id,vehicle_id,vehicle_plate,booking_type,status,trip_status,appointment_start,appointment_end) VALUES(@p0,@p1,@p2,@p3,@p3,'TEST-30T','dropoff','pending','Scheduled',@p4,@p5)",snapshotBooking.Id,carrier,driver,created.Id,start,end);
        context.Attach(snapshotBooking);
        foreach(var property in new[] {"VehicleSnapshot","DriverSnapshot","TrailerNumber","DriverName"}) context.Entry(snapshotBooking).Property(property).IsModified=true;
        await context.SaveChangesAsync();
        var stored=await context.Database.SqlQueryRaw<string>("SELECT driver_snapshot->>'Phone' AS \"Value\" FROM bookings WHERE id={0}",snapshotBooking.Id).SingleAsync();
        Check(stored=="0900000001","EF persists driver profile as queryable JSON in the booking database row");
        Check(await context.Database.SqlQueryRaw<string>("SELECT vehicle_snapshot->>'CurrentLocation' AS \"Value\" FROM bookings WHERE id={0}",snapshotBooking.Id).SingleAsync()=="Gate 02","EF persists full vehicle snapshot in the booking row");
        Check(await context.Database.SqlQueryRaw<string>("SELECT trailer_number AS \"Value\" FROM bookings WHERE id={0}",snapshotBooking.Id).SingleAsync()=="TEST-TRAILER","trailer plate is saved in a dedicated Booking field");
        var originalVehicle=snapshotBooking.VehicleSnapshot; var originalDriver=snapshotBooking.DriverSnapshot;
        await Write("UPDATE trucks SET plate_number='CHANGED-PLATE',photo_url='fleet/changed.png' WHERE id=@p0; UPDATE drivers SET full_name='Changed driver',phone='CHANGED-PHONE' WHERE id=@p1",created.Id,driver);
        await Capture();
        Check(snapshotBooking.VehicleSnapshot==originalVehicle&&snapshotBooking.DriverSnapshot==originalDriver,"editing source profiles or rescheduling never rewrites an existing assignment snapshot");
        var map=typeof(BookingService).GetMethod("MapToDto",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        var savedDto=(BookingDto)map.Invoke(null,new object[] {snapshotBooking})!;
        Check(savedDto.VehicleDetails!.PlateNumber=="TEST-30T"&&savedDto.DriverDetails!.Phone=="0900000001"&&savedDto.DriverDetails.CapturedAt.HasValue,"booking API exposes historical profiles instead of live changed values");
        snapshotBooking.TruckId=truck;
        await Capture();
        Check(snapshotBooking.VehicleSnapshot!=originalVehicle&&snapshotBooking.DriverSnapshot==originalDriver,"reassignment refreshes only the changed vehicle profile");
        snapshotBooking.DriverId=null;snapshotBooking.TruckId=null;
        await Capture();
        Check(snapshotBooking.DriverSnapshot==null&&snapshotBooking.VehicleSnapshot==null,"removing an assignment clears its profile");
        snapshotBooking.CarrierId=otherCarrier;snapshotBooking.DriverId=driver;
        try {await Capture();throw new Exception("Other tenant profile captured");}
        catch(NexusPort.Shared.Exceptions.ValidationException){count++;}
        count+=await BookingSaveChecks.RunAsync(context);
        Console.WriteLine($"PASS {count} PostgreSQL-backed Booking checks on isolated marked localhost cluster.");
    }
}

internal sealed class TestStorage : NexusPort.Infrastructure.ExternalServices.IS3StorageService
{
    public string GetPresignedUrl(string? value,int expireDays=7)=>value??"";
    public Task<string> UploadFileAsync(Stream stream,string fileName,string contentType,string folder="drivers",CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    public Task<string> UploadBytesAsync(byte[] bytes,string fileName,string contentType,string folder="drivers",CancellationToken cancellationToken=default)=>throw new NotSupportedException();
}

using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Services;
using NexusPort.Modules.Booking.Domain.Entities;

internal static class BookingScheduleChecks
{
    public static async Task<int> RunAsync(AppDbContext context,BookingService service,Guid carrier,Guid container,Guid declaration,Guid truck,Guid driver)
    {
        await using var transaction=await context.Database.BeginTransactionAsync();
        var count=0;
        void Check(bool valid,string message){if(!valid)throw new Exception("Schedule check: "+message);count++;}
        try
        {
            var otherContainer=Guid.NewGuid();var otherDeclaration=Guid.NewGuid();var extraTruck=Guid.NewGuid();var extraDriver=Guid.NewGuid();var booking=Guid.NewGuid();var visit=Guid.NewGuid();
            var start=DateTime.UtcNow.Date.AddDays(4).AddHours(1);var end=start.AddHours(2);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO containers(id,container_no,container_type_id) SELECT {0},'TEST-OTHER',container_type_id FROM containers WHERE id={1}",otherContainer,container);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO container_declarations(id,container_id,carrier_id,load_status,cargo_type,gross_weight_kg) VALUES({0},{1},{2},'full','general',24000)",otherDeclaration,otherContainer,carrier);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO drivers(id,carrier_id,full_name,license_number,status) VALUES({0},{1},'Second driver','TEST-LICENSE-2','active')",extraDriver,carrier);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO trucks(id,carrier_id,driver_id,plate_number,vehicle_type,status) VALUES({0},{1},{2},'TEST-SECOND','ROAD_TRUCK','active')",extraTruck,carrier,extraDriver);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO bookings(id,carrier_id,truck_id,driver_id,booking_type,status,trip_status,appointment_start,appointment_end,booking_code) VALUES({0},{1},{2},{3},'dropoff','pending','Scheduled',{4},{5},'TEST-SCHEDULE')",booking,carrier,truck,driver,start,end);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO booking_containers(booking_id,container_id,declaration_id) VALUES({0},{1},{2})",booking,otherContainer,otherDeclaration);
            var request=new BookingPlanningRequest {ContainerId=container,DeclarationId=declaration,BookingType="Dropoff",AppointmentStart=start,AppointmentEnd=end};
            var plan=await service.PlanBookingAsync(carrier,request,default);
            Check(plan.Schedule.IsAvailable&&plan.Fleet.RecommendedTruckId==extraTruck&&plan.Fleet.RecommendedDriverId==extraDriver,"another truck/container may book exactly the same time window");
            await context.Database.ExecuteSqlRawAsync("UPDATE trucks SET status='maintenance' WHERE id={0}",extraTruck);
            plan=await service.PlanBookingAsync(carrier,request,default);
            Check(!plan.Schedule.IsAvailable&&plan.Schedule.Alternatives.First().AppointmentStart==end,"when all trucks are busy, a free window starts at the preceding appointment end");
            await context.Database.ExecuteSqlRawAsync("UPDATE trucks SET status='active' WHERE id={0}",extraTruck);
            await context.Database.ExecuteSqlRawAsync("UPDATE container_declarations SET container_id={0} WHERE id={1}; UPDATE booking_containers SET container_id={0} WHERE booking_id={2}",container,otherDeclaration,booking);
            plan=await service.PlanBookingAsync(carrier,request,default);
            Check(!plan.Schedule.IsAvailable&&plan.Schedule.ExistingBookingId==booking,"same container remains blocked despite another free truck");
            Check(plan.Schedule.Alternatives.First().AppointmentStart==end,"finite container appointment suggests a non-overlapping time");
            request.AppointmentStart=end;request.AppointmentEnd=end.AddHours(2);
            Check((await service.PlanBookingAsync(carrier,request,default)).Schedule.IsAvailable,"touching endpoints are allowed, not treated as an overlap");
            request.AppointmentStart=start;request.AppointmentEnd=end;
            await context.Database.ExecuteSqlRawAsync("INSERT INTO container_visits(id,container_id,declaration_id,pickup_code,visit_status) VALUES({0},{1},{2},'TC-ABCD-EFAB-1234','planned'); UPDATE booking_containers SET container_visit_id={0} WHERE booking_id={3}",visit,container,otherDeclaration,booking);
            plan=await service.PlanBookingAsync(carrier,request,default);
            Check(!plan.Schedule.IsAvailable&&plan.Schedule.Alternatives.Count==0&&plan.Schedule.Message.Contains("Pickup"),"unfinished stay has no fabricated later free slot");
            var earlyPickup=new BookingPlanningRequest {ContainerId=container,BookingType="Pickup",YardCode="TC-ABCD-EFAB-1234",AppointmentStart=start.AddHours(-4),AppointmentEnd=start.AddHours(-2)};
            var pickupPlan=await service.PlanBookingAsync(carrier,earlyPickup,default);
            Check(!pickupPlan.Schedule.IsAvailable&&pickupPlan.Schedule.Alternatives.First().AppointmentStart==end,"early Pickup is offered a legal window after inbound end instead of a missing-container error");
            var pickup=Guid.NewGuid();
            await context.Database.ExecuteSqlRawAsync("INSERT INTO bookings(id,carrier_id,booking_type,status,trip_status,appointment_start,appointment_end,booking_code) VALUES({0},{1},'pickup','pending','Scheduled',{2},{3},'TEST-PICKUP'); INSERT INTO booking_containers(booking_id,container_id,declaration_id,container_visit_id) VALUES({0},{4},{5},{6})",pickup,carrier,end,end.AddHours(2),container,otherDeclaration,visit);
            plan=await service.PlanBookingAsync(carrier,request,default);
            Check(plan.Schedule.Alternatives.First().AppointmentStart==end.AddHours(2),"new stay starts only after the existing stay's planned pickup ends");
            await context.Database.ExecuteSqlRawAsync("UPDATE bookings SET status='canceled' WHERE id={0} OR id={1}",booking,pickup);
            Check((await service.PlanBookingAsync(carrier,request,default)).Schedule.IsAvailable,"canceled bookings and their empty planned stay do not block a new appointment");
            await context.Database.ExecuteSqlRawAsync("UPDATE bookings SET status='pending',carrier_id={0} WHERE id={1}",Guid.NewGuid(),booking);
            plan=await service.PlanBookingAsync(carrier,request,default);
            Check(!plan.Schedule.IsAvailable&&plan.Schedule.ExistingBookingId==null&&plan.Schedule.ExistingBookingCode==null,"other companies' booking identities are not exposed");
            await context.Database.ExecuteSqlRawAsync("UPDATE bookings SET status='canceled' WHERE id={0}",booking);
            var legacyBooking=Guid.NewGuid();
            await context.Database.ExecuteSqlRawAsync("INSERT INTO bookings(id,carrier_id,booking_type,status,trip_status,appointment_start,appointment_end,booking_code) VALUES({0},{1},'dropoff','pending','Scheduled',{2},{3},'TEST-LEGACY')",legacyBooking,carrier,start,end);
            var number=await context.Database.SqlQueryRaw<string>("SELECT container_no AS \"Value\" FROM containers WHERE id={0}",container).SingleAsync();
            context.Set<BookingOperation>().Add(new BookingOperation(legacyBooking,"Export",number,"GateIn"));
            await context.SaveChangesAsync();
            plan=await service.PlanBookingAsync(carrier,request,default);
            Check(!plan.Schedule.IsAvailable&&plan.Schedule.ExistingBookingId==legacyBooking,"legacy operation without container ID is matched by the canonical container number");
            await transaction.RollbackAsync();
            Console.WriteLine($"PASS {count} simultaneous-booking and alternate-window PostgreSQL checks.");
            return count;
        }
        finally{context.ChangeTracker.Clear();}
    }
}

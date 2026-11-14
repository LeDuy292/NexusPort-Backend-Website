using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using NexusPort.Infrastructure.Database;
using NexusPort.Infrastructure.ExternalServices;
using NexusPort.Modules.Booking.Application.DTOs;
using NexusPort.Modules.Booking.Application.Services;
using NexusPort.Modules.Driver.Infrastructure.Repositories;
using NexusPort.Modules.Identity.Application.DTOs;
using NexusPort.Modules.Identity.Application.Services;
using NexusPort.Modules.Vehicle.Application.DTOs;
using NexusPort.Modules.Vehicle.Application.Services;
using NexusPort.Modules.Vehicle.Infrastructure.Repositories;
using NexusPort.Shared.Exceptions;
using Npgsql;
using Driver = NexusPort.Modules.Driver.Domain.Entities.Driver;
using Vehicle = NexusPort.Modules.Vehicle.Domain.Entities.Vehicle;
using Booking = NexusPort.Modules.Booking.Domain.Entities.Booking;
using BookingStatus = NexusPort.Modules.Booking.Domain.Enums.BookingStatus;

var count = 0;
void Check(bool valid, string message) { if (!valid) throw new Exception(message); count++; }
async Task Reject<T>(Func<Task> action, string message) where T : Exception {
    try { await action(); throw new Exception(message); } catch (T) { count++; }
}
var invalid = new DriverAccountDto { Username = "!!", Email = "bad" };
Check(!Validator.TryValidateObject(invalid, new ValidationContext(invalid), new List<ValidationResult>(), true), "Invalid account rejected");
var activation = new ActivateDriverAccountDto { Token = "bad", Password = "short" };
Check(!Validator.TryValidateObject(activation, new ValidationContext(activation), new List<ValidationResult>(), true), "Invalid activation rejected");
var password = "DriverSecure123!";
var hash = BCrypt.Net.BCrypt.HashPassword(password, 12);
Check(BCrypt.Net.BCrypt.Verify(password, hash), "Bcrypt password round-trip");
Check(!BCrypt.Net.BCrypt.Verify("wrong", hash), "Wrong password rejected");
Check(new ForbiddenException("Company membership").StatusCode == 403, "Company authorization never means expired session");
if (!args.Contains("--postgres")) { Console.WriteLine($"PASS {count} fleet unit checks. Use --postgres for the isolated local fixture."); return; }

// Fixed local-only cluster and marker. This runner never loads appsettings or real account identities.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
var builder = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=55440;Database=fleet_check;Username=fleet_check;Timeout=3;Timezone=UTC");
builder.MapEnum<NexusPort.Modules.Driver.Domain.Enums.DriverStatus>("driver_status");
builder.MapEnum<NexusPort.Modules.Vehicle.Domain.Enums.TruckStatus>("truck_status");
builder.MapEnum<NexusPort.Modules.Booking.Domain.Enums.BookingType>("booking_type");
builder.MapEnum<BookingStatus>("booking_status");
await using var source = builder.Build();
await using var connection = await source.OpenConnectionAsync();
await using (var marker = new NpgsqlCommand("SELECT shobj_description(oid,'pg_database') FROM pg_database WHERE datname=current_database()", connection))
    Check((string?)await marker.ExecuteScalarAsync() == "isolated-fleet-checks-20261114", "Refuse an unmarked database");
var schema = "fleet_" + Guid.NewGuid().ToString("N");
await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}; SET search_path TO {schema};", connection)) await create.ExecuteNonQueryAsync();
async Task Sql(string text) { await using var cmd = new NpgsqlCommand(text, connection); await cmd.ExecuteNonQueryAsync(); }
try {
    await Sql("""
      CREATE TYPE driver_status AS ENUM ('active','inactive','banned','waiting_confirmation','receiving_vehicle','vehicle_received','booking_confirmed','transporting','transport_completed');
      CREATE TYPE truck_status AS ENUM ('active','inactive','maintenance');
      CREATE TYPE booking_type AS ENUM ('pickup','dropoff');
      CREATE TYPE booking_status AS ENUM ('pending','ready','approved','rejected','canceled','checked_in','completed','expired');
      CREATE TYPE container_status AS ENUM ('expected','discharged','in_yard','reserved','moving','gate_in','gate_out','loaded','damaged','canceled');
      CREATE TABLE users(id uuid PRIMARY KEY,username text UNIQUE,email text UNIQUE,password text,role text,full_name text,
        is_active boolean NOT NULL DEFAULT false,invitation_pending boolean NOT NULL DEFAULT false,email_verified_at timestamptz,
        session_id text,created_at timestamptz DEFAULT now(),updated_at timestamptz DEFAULT now());
      CREATE TABLE carriers(id uuid PRIMARY KEY,company_name text);
      CREATE TABLE carrier_users(carrier_id uuid,user_id uuid,PRIMARY KEY(carrier_id,user_id));
      CREATE TABLE account_requests(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),role text,carrier_id uuid,driver_id uuid,
        requested_by uuid,username text,email text,profile jsonb,status text,user_id uuid,created_at timestamptz DEFAULT now());
      CREATE TABLE account_invitations(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),user_id uuid,request_id uuid,
        token_hash char(64) UNIQUE,expires_at timestamptz,used_at timestamptz,created_at timestamptz DEFAULT now());
      CREATE TABLE gate_transactions(booking_id uuid,gate_type text,status text,checked_at timestamptz);
      CREATE TABLE transport_trips(id uuid,booking_id uuid,driver_id uuid,truck_id uuid,truck_plate text,trip_code text,booking_type text,status text,appointment_start timestamptz,appointment_end timestamptz);
      CREATE TABLE eport_service_drafts(id uuid,carrier_id uuid,number text,flow text,content jsonb);
      CREATE TABLE eport_service_order_states(draft_id uuid,status text);
      """);
    await connection.ReloadTypesAsync();
    var fixtureBuilder = new NpgsqlDataSourceBuilder($"Host=127.0.0.1;Port=55440;Database=fleet_check;Username=fleet_check;Search Path={schema};Timezone=UTC");
    fixtureBuilder.MapEnum<NexusPort.Modules.Driver.Domain.Enums.DriverStatus>("driver_status");
    fixtureBuilder.MapEnum<NexusPort.Modules.Vehicle.Domain.Enums.TruckStatus>("truck_status");
    fixtureBuilder.MapEnum<NexusPort.Modules.Booking.Domain.Enums.BookingType>("booking_type");
    fixtureBuilder.MapEnum<BookingStatus>("booking_status");
    fixtureBuilder.MapEnum<NexusPort.Modules.Container.Domain.Enums.ContainerStorageStatus>("container_status");
    await using var fixtureSource = fixtureBuilder.Build();
    await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixtureSource).Options);
    var types = new[] { typeof(Driver), typeof(Vehicle), typeof(Booking), typeof(NexusPort.Modules.Container.Domain.Entities.Container),
      typeof(NexusPort.Modules.Booking.Domain.Entities.BookingContainer),
      typeof(NexusPort.Modules.Booking.Domain.Entities.BookingOperation), typeof(NexusPort.Modules.Booking.Domain.Entities.BookingDocument) };
    foreach (var type in types) {
      var entity = db.Model.FindEntityType(type)!;
      var columns = entity.GetProperties().Select(p => $"\"{p.GetColumnName()}\" {p.GetColumnType()}");
      var keys = string.Join(',', entity.FindPrimaryKey()!.Properties.Select(p => $"\"{p.GetColumnName()}\""));
      await Sql($"CREATE TABLE \"{entity.GetTableName()}\" ({string.Join(',', columns)}, PRIMARY KEY({keys}))");
    }
    await Sql("ALTER TABLE drivers ADD COLUMN user_id uuid");
    var company = Guid.NewGuid(); var other = Guid.NewGuid(); var actor = Guid.NewGuid(); var outsider = Guid.NewGuid();
    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO carriers VALUES({company},'Fixture company'),({other},'Other fixture'); INSERT INTO users(id,username,email,role,is_active) VALUES({actor},'fixture-manager','manager@example.invalid','Transport Company',true),({outsider},'other-manager','other@example.invalid','Transport Company',true); INSERT INTO carrier_users VALUES({company},{actor}),({other},{outsider})");
    var mail = new RecordingMail();
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["AccountInvitations:FrontendOrigin"] = "https://example.invalid" }).Build();
    var accounts = new DriverAccountService(db, mail, config);
    DriverAccountDto Profile(string suffix) => new() { Username = "driver_" + suffix, Email = suffix + "@example.invalid", FullName = "Fixture " + suffix, Phone = "0900000000", IdCardNumber = "000000000001", LicenseNumber = "TEST-" + suffix };
    mail.Fail = true;
    await Reject<InvalidOperationException>(() => accounts.CreateAsync(actor, Profile("rollback"), default), "Failed email must roll back account");
    Check(!await db.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM users WHERE username='driver_rollback') AS \"Value\"").SingleAsync(), "No unusable account left after delivery failure");
    mail.Fail = false;
    await accounts.CreateAsync(actor, Profile("one"), default);
    var driver = await db.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM drivers WHERE license_number='TEST-one'").SingleAsync();
    var user = await db.Database.SqlQueryRaw<Guid>("SELECT user_id AS \"Value\" FROM drivers WHERE id={0}", driver).SingleAsync();
    Check(await db.Database.SqlQueryRaw<bool>("SELECT invitation_pending AND NOT is_active AND email_verified_at IS NULL AS \"Value\" FROM users WHERE id={0}", user).SingleAsync(), "New account pending flags are stored in DB");
    await Reject<ConflictException>(() => accounts.CreateAsync(actor, Profile("one"), default), "Duplicate username/email rejected");
    await Reject<NotFoundException>(() => accounts.ResendAsync(outsider, driver, false, default), "Other company cannot resend");
    var oldToken = mail.LastToken;
    await accounts.ResendAsync(actor, driver, false, default);
    await Reject<NexusPort.Shared.Exceptions.ValidationException>(() => accounts.ActivateAsync(new() { Token = oldToken, Password = password }, default), "Resend invalidates old link");
    await accounts.ActivateAsync(new() { Token = mail.LastToken, Password = password }, default);
    Check(await db.Database.SqlQueryRaw<bool>("SELECT NOT invitation_pending AND is_active AND email_verified_at IS NOT NULL AS \"Value\" FROM users WHERE id={0}", user).SingleAsync(), "Activation stores verified/active flags");
    Check(BCrypt.Net.BCrypt.Verify(password, await db.Database.SqlQueryRaw<string>("SELECT password AS \"Value\" FROM users WHERE id={0}", user).SingleAsync()), "Stored password matches existing bcrypt login");
    await Reject<NexusPort.Shared.Exceptions.ValidationException>(() => accounts.ActivateAsync(new() { Token = mail.LastToken, Password = password }, default), "Single-use link rejected on replay");
    await Reject<ConflictException>(() => accounts.ResendAsync(actor, driver, false, default), "Verified email cannot be resent");
    await accounts.CreateAsync(actor, Profile("expired"), default);
    await db.Database.ExecuteSqlRawAsync("UPDATE account_invitations SET expires_at=now()-interval '1 minute' WHERE used_at IS NULL");
    await Reject<NexusPort.Shared.Exceptions.ValidationException>(() => accounts.ActivateAsync(new() { Token = mail.LastToken, Password = password }, default), "Expired link rejected");
    var vehicle = new Vehicle(company, "TEST-01"); var second = new Vehicle(company, "TEST-02");
    var foreignVehicle = new Vehicle(other, "OTHER-01");
    db.Set<Vehicle>().AddRange(vehicle, second, foreignVehicle); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    var fleet = new VehicleService(new VehicleRepository(db), new NoBroker(), NullLogger<VehicleService>.Instance, new NoStorage(), db);
    await fleet.AssignDriverAsync(vehicle.Id, new() { DriverId = driver });
    await Reject<ConflictException>(() => fleet.AssignDriverAsync(second.Id, new() { DriverId = driver }), "Same driver cannot have two default vehicles");
    await Reject<NexusPort.Shared.Exceptions.ValidationException>(() => fleet.AssignDriverAsync(foreignVehicle.Id, new() { DriverId = driver }), "Cross-company default assignment rejected");
    Check((await new DriverRepository(db).GetAllAsync(new() { CarrierId = other })).Count == 0, "Driver list isolated by company");
    var service = new BookingService(null!, null!, null!, db, NullLogger<BookingService>.Instance, new BookingCompatibilityPolicy(), new BookingDocumentRequirementPolicy(), new NoStorage());
    var start = DateTime.UtcNow.Date.AddDays(3); var end = start.AddHours(2);
    var appointment = new Booking { CarrierId = company, BookingCode = "FIXTURE-BOOKING", DriverId = driver, TruckId = vehicle.Id, BookingType = NexusPort.Modules.Booking.Domain.Enums.BookingType.Dropoff, Status = BookingStatus.Approved, AppointmentStart = start, AppointmentEnd = end, TripStatus = "Scheduled" };
    db.Set<Booking>().Add(appointment); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    var schedule = await service.GetFleetScheduleAsync(company, start, end, default);
    Check(schedule.Items.Any(i => i.Id == appointment.Id && i.CanReassign && i.DriverName == "Fixture one"), "Schedule joins the assigned driver and allows pre-arrival reassignment");
    Check(!schedule.Drivers.Single(d => d.Id == driver).IsAvailable && !schedule.Vehicles.Single(v => v.Id == vehicle.Id).IsAvailable, "Overlapping booking blocks both resources");
    Check(schedule.Vehicles.Single(v => v.Id == second.Id).IsAvailable, "Unassigned active vehicle remains free");
    Check(schedule.Vehicles.All(v => v.Id != foreignVehicle.Id), "Other company fleet excluded");
    using var requestServices = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
    var http = new DefaultHttpContext { RequestServices = requestServices, User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, "Transport Company"),
        new Claim("CarrierId", other.ToString()) // A stale/spoofed company claim never overrides DB membership.
    }, "fixture")) };
    var driverController = new NexusPort.Modules.Driver.Presentation.Controllers.DriverController(
        new NexusPort.Modules.Driver.Application.Services.DriverService(new DriverRepository(db), new NoBroker(), new NoStorage(), NullLogger<NexusPort.Modules.Driver.Application.Services.DriverService>.Instance), null!, new NoStorage()) {
        ControllerContext = new ControllerContext { HttpContext = http }
    };
    var driverResponse = await driverController.GetAll(new() { CarrierId = other }, default);
    var driverRows = (IReadOnlyList<NexusPort.Modules.Driver.Application.DTOs.DriverDto>)((OkObjectResult)driverResponse.Result!).Value!;
    Check(driverRows.All(d => d.CarrierId == company), "Driver controller derives company from DB membership");
    Check(driverRows.Single(d => d.Id == driver) is { EmailVerified: true, AccountActive: true, InvitationPending: false, ActiveBookingCode: "FIXTURE-BOOKING" }, "Driver list includes stored account flags and active booking in C#");
    var vehicleController = new NexusPort.Modules.Vehicle.Presentation.Controllers.VehicleController(fleet, null!, null!, new NoStorage()) {
        ControllerContext = new ControllerContext { HttpContext = http }
    };
    var vehicleResponse = await vehicleController.GetAll(new() { CarrierId = other }, default);
    var vehicleRows = (IReadOnlyList<VehicleDto>)((OkObjectResult)vehicleResponse.Result!).Value!;
    Check(vehicleRows.Count == 2 && vehicleRows.All(v => v.CarrierId == company), "Vehicle controller does not expose another company's vehicles");
    Check(vehicleRows.Single(v => v.Id == vehicle.Id).DriverName == "Fixture one", "Vehicle list enriches only referenced drivers");
    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE drivers SET photo_url='NexusPort/drivers/photo.jpg',id_card_front_url='NexusPort/drivers/front.jpg',id_card_back_url='NexusPort/drivers/back.jpg',license_image_url='NexusPort/drivers/license-front.jpg',license_back_image_url='NexusPort/drivers/license-back.jpg' WHERE id={driver}; UPDATE trucks SET photo_url='NexusPort/vehicles/photo.jpg',registration_image_url='NexusPort/vehicles/registration.jpg' WHERE id={vehicle.Id}");
    db.ChangeTracker.Clear();
    var signing = new CountingStorage();
    var driverReader = new NexusPort.Modules.Driver.Application.Services.DriverService(new DriverRepository(db), new NoBroker(), signing, NullLogger<NexusPort.Modules.Driver.Application.Services.DriverService>.Instance);
    var driverList = await driverReader.GetAllAsync(new() { CarrierId = company, IncludeDocuments = false });
    Check(signing.Keys.Count == 1 && driverList.Single(d => d.Id == driver).IdCardFrontUrl == null, "Lightweight driver list signs the avatar only, not four identity documents");
    signing.Keys.Clear();
    var driverDetail = await driverReader.GetByIdAsync(driver);
    Check(signing.Keys.Count == 5 && driverDetail!.LicenseBackImageUrl != null, "Opening driver detail still loads all five images");
    signing.Keys.Clear();
    var vehicleReader = new VehicleService(new VehicleRepository(db), new NoBroker(), NullLogger<VehicleService>.Instance, signing, db);
    var vehicleList = await vehicleReader.GetAllAsync(new() { CarrierId = company, IncludeDocuments = false });
    Check(signing.Keys.Count == 1 && vehicleList.Single(v => v.Id == vehicle.Id).RegistrationImageUrl == null, "Lightweight vehicle list skips registration signing");
    signing.Keys.Clear();
    var vehicleDetail = await vehicleReader.GetByIdAsync(vehicle.Id);
    Check(signing.Keys.Count == 2 && vehicleDetail!.RegistrationImageUrl != null, "Opening vehicle detail loads the registration image");
    var unavailableImages = new UnavailableStorage();
    var noMediaDrivers = new NexusPort.Modules.Driver.Application.Services.DriverService(new DriverRepository(db), new NoBroker(), unavailableImages,
        NullLogger<NexusPort.Modules.Driver.Application.Services.DriverService>.Instance);
    var databaseDrivers = await noMediaDrivers.GetAllAsync(new() { CarrierId = company, IncludeDocuments = false, IncludePhotos = false });
    Check(databaseDrivers.Single(d => d.Id == driver) is { HasPhoto: true, PhotoUrl: null }, "Driver data loads even when AWS credentials are unavailable");
    var noMediaVehicles = new VehicleService(new VehicleRepository(db), new NoBroker(), NullLogger<VehicleService>.Instance, unavailableImages, db);
    var databaseVehicles = await noMediaVehicles.GetAllAsync(new() { CarrierId = company, IncludeDocuments = false, IncludePhotos = false });
    Check(databaseVehicles.Single(v => v.Id == vehicle.Id) is { HasPhoto: true, PhotoUrl: null }, "Vehicle data loads without calling AWS signing");
    var foreignDriver = new Driver(other, "Other fixture driver", "TEST-FOREIGN") { PhotoUrl = "NexusPort/drivers/foreign-photo.jpg" };
    db.Set<Driver>().Add(foreignDriver); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    var driverPhotos = (OkObjectResult)await driverController.GetPhotos(new[] { driver, foreignDriver.Id }, default);
    Check(((Dictionary<Guid, string>)driverPhotos.Value!).Keys.SequenceEqual(new[] { driver }), "Optional driver photo endpoint enforces company membership");
    var vehiclePhotos = (OkObjectResult)await vehicleController.GetPhotos(new[] { vehicle.Id, foreignVehicle.Id }, default);
    Check(((Dictionary<Guid, string>)vehiclePhotos.Value!).Keys.SequenceEqual(new[] { vehicle.Id }), "Optional vehicle photo endpoint excludes other company assets");
    Check((await service.GetFleetScheduleAsync(company, end, end.AddHours(2), default)).Drivers.Single(d => d.Id == driver).IsAvailable, "Back-to-back appointments do not overlap");
    Check((await service.GetAssignmentOptionsAsync(appointment.Id, company, default)).Drivers.Any(d => d.Id == driver), "Editing excludes the current booking from conflicts");
    await Reject<NotFoundException>(() => service.GetAssignmentOptionsAsync(appointment.Id, other, default), "Cannot get another company's assignment options");
    var container = new NexusPort.Modules.Container.Domain.Entities.Container("FIXTURE-CONTAINER");
    db.Set<NexusPort.Modules.Container.Domain.Entities.Container>().Add(container);
    db.Set<NexusPort.Modules.Booking.Domain.Entities.BookingContainer>().Add(new(appointment.Id, container.Id));
    await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    var mutationService = new BookingService(new NexusPort.Modules.Booking.Infrastructure.Repositories.BookingRepository(db), null!, null!, db,
        NullLogger<BookingService>.Instance, new BookingCompatibilityPolicy(), new BookingDocumentRequirementPolicy(), new NoStorage());
    var updated = await mutationService.AssignResourcesAsync(appointment.Id, new() { DriverId = driver, TruckId = second.Id, ContainerIds = new() { container.Id } }, company);
    Check(updated.TruckId == second.Id && updated.Status == BookingStatus.Approved, "Reassignment persists the new vehicle and preserves Approved status");
    Check((await db.Set<Vehicle>().AsNoTracking().SingleAsync(v => v.Id == vehicle.Id)).DriverId == driver, "Booking reassignment does not change the default vehicle-driver pair");
    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO transport_trips(id,booking_id,driver_id,truck_id,truck_plate,trip_code,status,appointment_start,appointment_end) VALUES({Guid.NewGuid()},{appointment.Id},{user},{second.Id},'TEST-02','FIXTURE-TRIP','Assigned',{start},{end})");
    db.ChangeTracker.Clear();
    Check(!(await service.GetFleetScheduleAsync(company, start, end, default)).Items.Single(i => i.Source == "Booking").CanReassign, "Issued trip disables booking reassignment");
    await Reject<NexusPort.Shared.Exceptions.ValidationException>(() => service.GetAssignmentOptionsAsync(appointment.Id, company, default), "Cannot edit an issued transport trip");
    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM transport_trips WHERE booking_id={appointment.Id}");
    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bookings SET status='checked_in' WHERE id={appointment.Id}");
    schedule = await service.GetFleetScheduleAsync(company, end.AddDays(1), end.AddDays(1).AddHours(2), default);
    Check(schedule.Items.Any(i => i.Id == appointment.Id && !i.CanReassign) && !schedule.Drivers.Single(d => d.Id == driver).IsAvailable, "Unfinished physical visit remains busy after planned end");
    await Reject<NexusPort.Shared.Exceptions.ValidationException>(() => service.GetAssignmentOptionsAsync(appointment.Id, company, default), "Cannot edit a checked-in booking");
    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bookings SET status='canceled' WHERE id={appointment.Id}");
    schedule = await service.GetFleetScheduleAsync(company, start, end, default);
    Check(!schedule.Items.Any() && schedule.Drivers.Single(d => d.Id == driver).IsAvailable, "Canceled booking releases schedule");
    var content = System.Text.Json.JsonSerializer.Serialize(new { delivery = new { trips = new[] { new { driverId = driver, truckId = vehicle.Id, appointmentStart = start.ToString("O"), appointmentEnd = end.ToString("O") } } } });
    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO eport_service_drafts VALUES({Guid.NewGuid()},{company},'FIXTURE-SERVICE','EXPORT',{content}::jsonb)");
    schedule = await service.GetFleetScheduleAsync(company, start, end, default);
    Check(schedule.Items.Single().Source == "Service" && !schedule.Drivers.Single(d => d.Id == driver).IsAvailable, "Service reservations appear and block availability");
    await Reject<NexusPort.Shared.Exceptions.ValidationException>(() => service.GetFleetScheduleAsync(company, start, start.AddHours(25), default), "Oversized schedule windows rejected");
    Console.WriteLine($"PASS {count} fleet/account checks on synthetic local PostgreSQL. No real email or account used.");
} finally {
    await Sql($"SET search_path TO public; DROP SCHEMA {schema} CASCADE;");
}

sealed class RecordingMail : IAccountInvitationEmail {
    public bool Fail { get; set; }
    public string LastToken { get; private set; } = "";
    public Task SendAsync(string email, string activationUrl, CancellationToken token) {
      if (Fail) throw new InvalidOperationException("Fixture delivery failed");
      LastToken = new Uri(activationUrl).Fragment[7..]; return Task.CompletedTask;
    }
}
sealed class NoBroker : IMessageBrokerService {
    public Task PublishAsync<T>(string queue, T value, CancellationToken token = default) => Task.CompletedTask;
}
sealed class NoStorage : IS3StorageService {
    public string GetPresignedUrl(string? key, int days = 7) => key ?? "";
    public Task<string> UploadFileAsync(Stream s, string name, string type, string folder = "drivers", CancellationToken token = default) => throw new NotSupportedException();
    public Task<string> UploadBytesAsync(byte[] b, string name, string type, string folder = "drivers", CancellationToken token = default) => throw new NotSupportedException();
}
sealed class CountingStorage : IS3StorageService {
    public List<string> Keys { get; } = new();
    public string GetPresignedUrl(string? key, int days = 7) { if (!string.IsNullOrEmpty(key)) Keys.Add(key); return key ?? ""; }
    public Task<string> UploadFileAsync(Stream s, string name, string type, string folder = "drivers", CancellationToken token = default) => throw new NotSupportedException();
    public Task<string> UploadBytesAsync(byte[] b, string name, string type, string folder = "drivers", CancellationToken token = default) => throw new NotSupportedException();
}
sealed class UnavailableStorage : IS3StorageService {
    public string GetPresignedUrl(string? key, int days = 7) => throw new InvalidOperationException("AWS signing must not run on the data-list path");
    public Task<string> UploadFileAsync(Stream s, string name, string type, string folder = "drivers", CancellationToken token = default) => throw new NotSupportedException();
    public Task<string> UploadBytesAsync(byte[] b, string name, string type, string folder = "drivers", CancellationToken token = default) => throw new NotSupportedException();
}

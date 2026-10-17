using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using NexusPort.Api.Extensions;
using NexusPort.Api.Middleware;
using NexusPort.Modules.Carrier;

var builder = WebApplication.CreateBuilder(args);

// Fix Npgsql DateTime timezone conversion error
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>

    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .AddApplicationPart(typeof(NexusPort.Modules.Identity.Presentation.Controllers.IdentityController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Booking.Presentation.Controllers.BookingController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Vessel.Presentation.Controllers.VesselController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Berth.Presentation.Controllers.BerthController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Container.Presentation.Controllers.ContainerController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Yard.Presentation.Controllers.YardController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Gate.Presentation.Controllers.GateController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Dispatcher.Presentation.Controllers.DispatcherController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Vehicle.Presentation.Controllers.VehicleController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Driver.Presentation.Controllers.DriverController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.Equipment.Presentation.Controllers.EquipmentController).Assembly);

// Add SignalR
builder.Services.AddSignalR();

// Add Infrastructure & Domain Modules
builder.Services.AddNexusPortInfrastructure(builder.Configuration);
builder.Services.AddNexusPortModules();
builder.Services.AddCarrierModule(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Host=localhost;Port=5432;Database=NexusPort;Username=postgres;Password=pgadmin4");
builder.Services.AddSwaggerDocumentation();

// Ensure wwwroot exists for StaticFiles middleware
var webRoot = builder.Environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
if (!Directory.Exists(webRoot)) {
    Directory.CreateDirectory(webRoot);
}

builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("booking-create", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "NexusPort API v1 (.NET 8)");
        c.RoutePrefix = string.Empty; // Swagger at root /
    });
}

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

app.UseCors("NexusPortCorsPolicy");

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<NexusPort.Modules.Yard.Infrastructure.Hubs.ReeferMonitoringHub>("/hubs/reefer-monitoring");

app.Run();

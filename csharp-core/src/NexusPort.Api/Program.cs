using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using NexusPort.Api.Extensions;
using NexusPort.Api.Middleware;
using NexusPort.Modules.Carrier;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();
ApplyDeploymentConfigFallbacks(builder.Configuration);

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
    .AddApplicationPart(typeof(NexusPort.Modules.Equipment.Presentation.Controllers.EquipmentController).Assembly)
    .AddApplicationPart(typeof(NexusPort.Modules.ServiceCatalog.Presentation.ServiceCatalogController).Assembly);

// Add SignalR
builder.Services.AddSignalR();

// Add Infrastructure & Domain Modules
builder.Services.AddNexusPortInfrastructure(builder.Configuration);
builder.Services.AddNexusPortModules();
builder.Services.AddScoped<NexusPort.Modules.ServiceCatalog.Application.CatalogService>();
builder.Services.AddScoped<NexusPort.Modules.ServiceCatalog.Application.ServiceDraftService>();
builder.Services.AddScoped<NexusPort.Modules.Booking.Application.Services.IBookingFleetAvailability>(sp =>
    (NexusPort.Modules.Booking.Application.Services.BookingService)sp.GetRequiredService<NexusPort.Modules.Booking.Application.Interfaces.IBookingService>());
builder.Services.AddScoped<NexusPort.Modules.ServiceCatalog.Application.IServiceWorkflowEmail, NexusPort.Modules.ServiceCatalog.Application.ServiceWorkflowEmail>();
builder.Services.AddScoped<NexusPort.Modules.ServiceCatalog.Application.ServiceOrderService>();
builder.Services.AddScoped<NexusPort.Modules.ServiceCatalog.Application.ServiceMailQueue>();
builder.Services.AddHostedService<NexusPort.Modules.ServiceCatalog.Application.ServiceMailWorker>();
builder.Services.AddHttpClient<NexusPort.Modules.ServiceCatalog.Application.IPayOsGateway, NexusPort.Modules.ServiceCatalog.Application.PayOsGateway>(http => http.Timeout = TimeSpan.FromSeconds(12));
builder.Services.AddCarrierModule(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Host=localhost;Port=5432;Database=NexusPort;Username=postgres;Password=pgadmin4");
builder.Services.AddSwaggerDocumentation();

// Ensure wwwroot exists for StaticFiles middleware
var webRoot = builder.Environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
if (!Directory.Exists(webRoot)) {
    Directory.CreateDirectory(webRoot);
}

builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.AddPolicy("account-activation", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("booking-create", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() ||
    string.Equals(Environment.GetEnvironmentVariable("ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase))
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

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "NexusPort C# Core API (.NET 8)",
    timestamp = DateTime.UtcNow
})).AllowAnonymous();

app.MapControllers();
app.MapHub<NexusPort.Modules.Yard.Infrastructure.Hubs.ReeferMonitoringHub>("/hubs/reefer-monitoring");

app.Run();

static void ApplyDeploymentConfigFallbacks(ConfigurationManager config)
{
    if (string.IsNullOrWhiteSpace(config["ConnectionStrings:DefaultConnection"]))
    {
        var npgsqlConn = ResolvePostgresConnectionString();
        if (!string.IsNullOrWhiteSpace(npgsqlConn))
        {
            config["ConnectionStrings:DefaultConnection"] = npgsqlConn;
        }
    }

    var envJwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET");
    if (!string.IsNullOrWhiteSpace(envJwtSecret) &&
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Jwt__Secret")))
    {
        config["Jwt:Secret"] = envJwtSecret;
    }
}

static string? ResolvePostgresConnectionString()
{
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrWhiteSpace(databaseUrl) && Uri.TryCreate(databaseUrl.Trim(), UriKind.Absolute, out var uri))
    {
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty;
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var port = uri.Port > 0 ? uri.Port : 5432;
        var database = uri.AbsolutePath.TrimStart('/');
        var disableSsl = uri.Query.Contains("sslmode=disable", StringComparison.OrdinalIgnoreCase);
        var sslPart = disableSsl ? "SSL Mode=Disable" : "SSL Mode=Require;Trust Server Certificate=true";
        var pool = Environment.GetEnvironmentVariable("DB_POOL_MAX") ?? "5";

        return $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password};{sslPart};Maximum Pool Size={pool};Connection Idle Lifetime=15;Connection Pruning Interval=5";
    }

    var dbHost = Environment.GetEnvironmentVariable("DB_HOST");
    var dbUser = Environment.GetEnvironmentVariable("DB_USER");
    if (!string.IsNullOrWhiteSpace(dbHost) && !string.IsNullOrWhiteSpace(dbUser))
    {
        var dbPort = Environment.GetEnvironmentVariable("DB_PORT") ?? "5432";
        var dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "nexusport";
        var dbPass = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? string.Empty;
        var poolMax = Environment.GetEnvironmentVariable("DB_POOL_MAX") ?? "5";
        var sslMode = Environment.GetEnvironmentVariable("DB_SSL_MODE") ?? "Disable";
        return $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPass};SSL Mode={sslMode};Maximum Pool Size={poolMax};Connection Idle Lifetime=15;Connection Pruning Interval=5";
    }

    return null;
}

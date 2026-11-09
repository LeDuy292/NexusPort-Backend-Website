using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.ServiceCatalog.Application;
using NexusPort.Modules.ServiceCatalog.Presentation;
using Npgsql;
using System.Text;

var connection = Environment.GetEnvironmentVariable("SERVICE_DRAFT_TEST_CONNECTION")
    ?? throw new Exception("Set SERVICE_DRAFT_TEST_CONNECTION for the isolated checker.");
var target = new NpgsqlConnectionStringBuilder(connection);
if (target.Host != "127.0.0.1" || target.Port != 55444 || target.Database != "eport_drafts_check" || target.Username != "draft_check")
    throw new Exception("Refusing all databases except isolated 127.0.0.1:55444/eport_drafts_check as draft_check.");
await using var pg = new NpgsqlConnection(connection);
await pg.OpenAsync();
var marker = Environment.GetEnvironmentVariable("SERVICE_DRAFT_TEST_MARKER");
await using (var command = new NpgsqlCommand("SELECT current_setting('nexusport.drafts_test',true)", pg))
    if (string.IsNullOrWhiteSpace(marker) || (string?)await command.ExecuteScalarAsync() != marker)
        throw new Exception("Isolated draft database marker mismatch.");

await DraftFixtures.Prepare(pg);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5510");
builder.Services.AddControllers().AddApplicationPart(typeof(ServiceDraftsController).Assembly);
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ServiceDraftService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    options.TokenValidationParameters = new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DraftFixtures.Secret)),
        ValidateIssuer = false, ValidateAudience = false,
    });
builder.Services.AddAuthorization();
var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception error)
    {
        Console.WriteLine($"TEST API ERROR: {error.GetType().Name}: {error.Message}");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { message = error.Message });
    }
});
app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
await app.StartAsync();
try
{
    await DraftHttpChecks.Run(pg);
    Console.WriteLine("Fixture IDs: customer=" + DraftFixtures.Id(1) + "; staff=" + DraftFixtures.Id(3)
        + "; otherCustomer=" + DraftFixtures.Id(2) + "; exportBooking=" + DraftFixtures.Id(201)
        + "; exportDry=" + DraftFixtures.Id(301) + "; exportReefer=" + DraftFixtures.Id(304)
        + "; ownVisit=" + DraftFixtures.Id(401) + "; sharedVisit=" + DraftFixtures.Id(403));
    if (args.Contains("--serve"))
    {
        Console.WriteLine("Isolated service-draft HTTP server ready at http://127.0.0.1:5510");
        await app.WaitForShutdownAsync();
    }
}
finally { await app.StopAsync(); await app.DisposeAsync(); }

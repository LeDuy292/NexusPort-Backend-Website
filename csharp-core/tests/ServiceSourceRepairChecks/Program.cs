using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.ServiceCatalog.Application;
using NexusPort.Modules.ServiceCatalog.Presentation;
using Npgsql;
using System.Text;

var connection = Environment.GetEnvironmentVariable("SOURCE_REPAIR_TEST_CONNECTION")
    ?? throw new Exception("Set SOURCE_REPAIR_TEST_CONNECTION for the isolated checker.");
var target = new NpgsqlConnectionStringBuilder(connection);
if (target.Host != "127.0.0.1" || target.Port != 55446 || target.Database != "eport_source_repair_check" || target.Username != "source_repair_check")
    throw new Exception("Refusing all databases except isolated 127.0.0.1:55446/eport_source_repair_check as source_repair_check.");
await using var pg = new NpgsqlConnection(connection);
await pg.OpenAsync();
var marker = Environment.GetEnvironmentVariable("SOURCE_REPAIR_TEST_MARKER");
await using (var command = new NpgsqlCommand("SELECT current_setting('nexusport.source_repair_test',true)", pg))
    if (string.IsNullOrWhiteSpace(marker) || (string?)await command.ExecuteScalarAsync() != marker)
        throw new Exception("Isolated source-repair database marker mismatch.");

await SourceRepairFixtures.PrepareLegacy(pg);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5514");
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
    await SourceRepairHttpChecks.Run(pg);
    if (args.Contains("--serve"))
    {
        Console.WriteLine("Isolated source-repair HTTP server ready at http://127.0.0.1:5514");
        await app.WaitForShutdownAsync();
    }
}
finally { await app.StopAsync(); await app.DisposeAsync(); }

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.ServiceCatalog.Application;
using NexusPort.Modules.ServiceCatalog.Presentation;
using Npgsql;

// This opt-in checker uses the project's real DEVELOPMENT connection, never an
// alternative database. Its only writes are two deterministic DEMO registrations.
if (Environment.GetEnvironmentVariable("SERVICE_SOURCE_LIVE_CHECK") != "development")
    throw new Exception("Set SERVICE_SOURCE_LIVE_CHECK=development explicitly after deployment and sample insertion.");
var location = new DirectoryInfo(Environment.CurrentDirectory);
while (location != null && !File.Exists(Path.Combine(location.FullName, "csharp-core", "src", "NexusPort.Api", "appsettings.Development.json"))) location = location.Parent;
var repo = location?.FullName ?? throw new Exception("Run this checker from inside the backend repository.");
var envFile = Path.Combine(repo, ".env");
var configFile = Path.Combine(repo, "csharp-core", "src", "NexusPort.Api", "appsettings.Development.json");
if (!File.Exists(envFile) || !File.Exists(configFile)) throw new Exception("Run from the backend repository.");
var environment = File.ReadAllLines(envFile).Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#') && line.Contains('='))
    .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim().Trim('"', '\''));
if (environment.GetValueOrDefault("NODE_ENV") != "development" || environment.GetValueOrDefault("DB_TARGET") != "aiven")
    throw new Exception("This checker refuses non-development targets.");
var config = JsonNode.Parse(await File.ReadAllTextAsync(configFile))!;
var connection = config["ConnectionStrings"]!["DefaultConnection"]!.GetValue<string>();
var target = new NpgsqlConnectionStringBuilder(connection);
if (target.Host != environment.GetValueOrDefault("AIVEN_DB_HOST") || target.Database != environment.GetValueOrDefault("AIVEN_DB_NAME"))
    throw new Exception("Configured C# target must equal the configured development database.");
await using var pg = new NpgsqlConnection(connection);
await pg.OpenAsync();
await using (var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM nexusport_schema_versions WHERE version='20261109_038')", pg))
    if (await check.ExecuteScalarAsync() is not true) throw new Exception("Migration038 must be deployed before this read-only preflight.");

var users = new List<LiveUser>();
await using (var read = new NpgsqlCommand("""
    SELECT u.id,u.username,u.role,cu.carrier_id FROM users u JOIN carrier_users cu ON cu.user_id=u.id
    WHERE u.is_active AND u.username IN ('carrier01','dauboquay') AND u.role='Transport Company'
      AND (SELECT count(*) FROM carrier_users x WHERE x.user_id=u.id)=1 ORDER BY u.username
    """, pg))
await using (var rows = await read.ExecuteReaderAsync())
    while (await rows.ReadAsync())
    {
        var company = rows.GetGuid(3);
        var hash = Convert.ToHexString(SHA256.HashData(company.ToByteArray())).ToLowerInvariant();
        users.Add(new(rows.GetGuid(0), rows.GetString(1), rows.GetString(2), company, Guid.Parse("03820000-" + hash[..4] + "-4000-8000-" + hash[4..16])));
    }
if (users.Count != 2 || users.Select(user => user.CarrierId).Distinct().Count() != 2)
    throw new Exception("The two expected explicitly scoped demonstration companies are required.");
foreach (var user in users)
{
    await using var check = new NpgsqlCommand("""
        SELECT count(*) FROM export_bookings b WHERE b.carrier_id=@company AND b.id::text LIKE '03810000-%'
          AND b.booking_no LIKE 'DEMO-EPORT-%' AND b.status='ContainersDeclared' AND b.port_status='Accepted'
          AND (SELECT count(*) FROM export_booking_containers e WHERE e.export_booking_id=b.id AND e.active AND e.status='Expected')=2
        """, pg);
    check.Parameters.AddWithValue("company", user.CarrierId);
    if (Convert.ToInt32(await check.ExecuteScalarAsync()) != 3) throw new Exception("Each company must already have three seeded two-container DEMO sources. No write performed.");
    await using var existing = new NpgsqlCommand("SELECT carrier_id,content->>'note' FROM eport_service_drafts WHERE id=@id", pg);
    existing.Parameters.AddWithValue("id", user.DraftId);
    await using var found = await existing.ExecuteReaderAsync();
    if (await found.ReadAsync() && (found.GetGuid(0) != user.CarrierId || found.GetString(1) != LiveUser.Note))
        throw new Exception("Reserved demo verification identity is already in use; no write performed.");
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5516");
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
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(LiveUser.TestSecret)),
        ValidateIssuer = false, ValidateAudience = false,
    });
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    var idClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? context.User.FindFirst("sub")?.Value ?? context.User.FindFirst("id")?.Value;
    var user = users.SingleOrDefault(user => user.Id.ToString() == idClaim);
    if (user == null) { context.Response.StatusCode = 403; return; }
    if (context.Request.Method is not "GET")
    {
        var path = context.Request.Path.Value ?? "";
        var allowed = new[] { $"/api/v1/ServiceDrafts/{user.DraftId}", $"/api/v1/ServiceDrafts/{user.DraftId}/validate", $"/api/v1/ServiceDrafts/{user.DraftId}/confirm" };
        if (!allowed.Contains(path, StringComparer.OrdinalIgnoreCase) || (context.Request.Method != "PUT" && context.Request.Method != "POST"))
        { context.Response.StatusCode = 403; return; }
        if (context.Request.Method == "PUT")
        {
            context.Request.EnableBuffering();
            var body = await JsonNode.ParseAsync(context.Request.Body);
            context.Request.Body.Position = 0;
            if (body?["flow"]?.GetValue<string>() != "EXPORT" || body?["note"]?.GetValue<string>() != LiveUser.Note
                || !Guid.TryParse(body?["source"]?["bookingId"]?.GetValue<string>(), out var bookingId))
            { context.Response.StatusCode = 403; return; }
            await using var sourceCheck = new NpgsqlConnection(connection);
            await sourceCheck.OpenAsync();
            await using var exact = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM export_bookings WHERE id=@id AND carrier_id=@company AND id::text LIKE '03810000-%' AND booking_no LIKE 'DEMO-EPORT-%')", sourceCheck);
            exact.Parameters.AddWithValue("id", bookingId); exact.Parameters.AddWithValue("company", user.CarrierId);
            if (await exact.ExecuteScalarAsync() is not true) { context.Response.StatusCode = 403; return; }
        }
    }
    await next();
});
app.MapControllers();
app.MapGet("/__live-check/users", () => users.Select(user => new { id = user.Id, username = user.Username, role = user.Role, carrierId = user.CarrierId, draftId = user.DraftId }));
await app.StartAsync();
Console.WriteLine("Real development service checker ready at 127.0.0.1:5516; guarded DEMO source writes only.");
try { await app.WaitForShutdownAsync(); }
finally { await app.StopAsync(); await app.DisposeAsync(); }

sealed record LiveUser(Guid Id, string Username, string Role, Guid CarrierId, Guid DraftId)
{
    public const string Note = "DEMO e-Port verification";
    // Only this loopback checker accepts this fixture key. It is not an app credential.
    public const string TestSecret = "service-live-check-loopback-only-demo-test-key-2026";
}

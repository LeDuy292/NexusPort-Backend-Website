using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPort.Infrastructure.Authentication;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.ServiceCatalog.Application;
using NexusPort.Modules.ServiceCatalog.Domain;
using NexusPort.Modules.ServiceCatalog.Presentation;

var connection = Environment.GetEnvironmentVariable("CATALOG_TEST_CONNECTION") ?? throw new Exception("Set the isolated catalog test connection.");
var config = new NpgsqlConnectionStringBuilder(connection);
Check(config.Host == "127.0.0.1" && config.Port == 55443 && config.Database == "eport_catalog_check" && config.Username == "catalog_check", "Refusing any database except isolated localhost:55443/eport_catalog_check.");
await using var pg = new NpgsqlConnection(connection); await pg.OpenAsync();
await using (var marker = new NpgsqlCommand("SELECT current_setting('nexusport.catalog_test',true)", pg)) Check((string?)await marker.ExecuteScalarAsync() == Environment.GetEnvironmentVariable("CATALOG_TEST_MARKER") && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CATALOG_TEST_MARKER")), "Test cluster marker mismatch.");
await using (var schema = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS users(id uuid PRIMARY KEY,role text NOT NULL,is_active boolean NOT NULL DEFAULT true);CREATE TABLE IF NOT EXISTS nexusport_schema_versions(version text PRIMARY KEY,description text NOT NULL);", pg)) await schema.ExecuteNonQueryAsync();
var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "catalog.sql"));
for (var i = 0; i < 2; i++) { await using var apply = new NpgsqlCommand(migration, pg); await apply.ExecuteNonQueryAsync(); }
var adminId = Guid.Parse("03510000-0000-4000-8000-000000000001");
var customerId = Guid.Parse("03510000-0000-4000-8000-000000000002");
await using (var users = new NpgsqlCommand("INSERT INTO users(id,role) VALUES(@a,'Administrator'),(@c,'Transport Company') ON CONFLICT(id) DO NOTHING", pg)) { users.Parameters.AddWithValue("a", adminId); users.Parameters.AddWithValue("c", customerId); await users.ExecuteNonQueryAsync(); }
var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
await using var db = new AppDbContext(options);
var admin = new CatalogService(db, new TestUser(adminId, "Administrator"));
var customer = new CatalogService(db, new TestUser(customerId, "Transport Company"));
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var list = JsonSerializer.SerializeToNode(await admin.List(default), json)!;
Check(list["services"]!.AsArray().Count == 6, "Migration is idempotent and installs six reference services.");
var id = Guid.Parse("03500000-0000-4000-8000-000000000001");
var noPrice = await customer.Quote(id, new("20FT", "DRY", 1), default);
Check(noPrice.Status == "UNPRICED" && noPrice.Total == null, "Missing prices are never zero/free prices.");
await Expect(403, () => customer.AddTariff(id, new() { Amount = 100, EffectiveFrom = CatalogRules.Today }, default));
await Expect(422, () => admin.AddTariff(id, new() { Amount = -1, EffectiveFrom = CatalogRules.Today }, default));
await Expect(422, () => admin.AddTariff(id, new() { Amount = 1, EffectiveFrom = "2026-02-30" }, default));
var rate = await admin.AddTariff(id, new() { Amount = 100, EffectiveFrom = CatalogRules.Today, Note = "Isolated test fixture, not an actual terminal tariff" }, default);
await Expect(409, () => admin.AddTariff(id, new() { Amount = 200, EffectiveFrom = CatalogRules.Today }, default));
await admin.AddTariff(id, new() { Size = "20FT", Equipment = "DRY", Amount = 125, EffectiveFrom = CatalogRules.Today }, default);
var priced = await customer.Quote(id, new("20FT", "DRY", 2), default);
Check(priced.UnitPrice == 125 && priced.Total == 250, "Specific size/equipment price wins over general price.");
await Expect(422, () => customer.Quote(id, new("20FT", "DRY", 1.25m), default));
var saved = await admin.Save(null, new() { Code = "CHECK_SERVICE", Name = "Dịch vụ kiểm thử", Description = "Chỉ trong database kiểm thử", Conditions = ["Điều kiện kiểm thử"] }, default);
var savedId = Guid.Parse(saved["id"]!.ToString());
await admin.Save(savedId, new() { Code = "CHECK_SERVICE", Name = "Đã đổi tên", Description = "Chỉ trong database kiểm thử", Conditions = ["Điều kiện kiểm thử"], ExpectedVersion = 1 }, default);
await Expect(409, () => admin.Save(savedId, new() { Code = "CHECK_SERVICE", Name = "Ghi đè cũ", Description = "Chỉ trong database kiểm thử", Conditions = ["Điều kiện kiểm thử"], ExpectedVersion = 1 }, default));
await admin.Retire(id, Guid.Parse(rate["id"]!.ToString()), default);
await admin.Retire(id, Guid.Parse(rate["id"]!.ToString()), default);
Check(((List<JsonObject>)await admin.History(id, default)).Count(r => r["action"]!.ToString() == "TARIFF_RETIRED") == 1, "Repeated retirement does not duplicate audit entries.");
await admin.AddTariff(id, new() { Amount = 0, EffectiveFrom = CatalogRules.Today }, default);
Check((await customer.Quote(id, new("40FT", "DRY", 1), default)).Total == 0, "An explicitly configured zero price is preserved.");
var concurrent = await Task.WhenAll(Attempt(), Attempt());
Check(concurrent.Count(x => x) == 1, "Concurrent overlapping rates are serialized; exactly one succeeds.");
var current = JsonSerializer.SerializeToNode(await customer.List(default), json)!;
Check(current["services"]!.AsArray().SelectMany(s => s!["tariffs"]!.AsArray()).All(r => r!["isActive"]!.GetValue<bool>()), "Customer list excludes retired tariffs.");
var future = new PriceOption(Guid.NewGuid(), "20FT", "DRY", 99, "2099-01-01", null, true);
Check(CatalogRules.Quote(true, "CONTAINER", [future], new("20FT", "DRY", 1), "2026-11-08").Status == "UNPRICED", "Future rates do not apply early.");
Check(CatalogRules.Quote(false, "HOUR", [], new("20FT", "REEFER", .5m)).Status == "SUSPENDED", "Suspended service cannot be estimated.");
Console.WriteLine("Catalog domain/PostgreSQL checks passed; shared databases were not contacted.");

if (args.Contains("--serve"))
{
    const string secret = "catalog-isolated-test-secret-at-least-32-characters";
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls("http://127.0.0.1:5508");
    builder.Services.AddControllers().AddApplicationPart(typeof(ServiceCatalogController).Assembly);
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
    builder.Services.AddHttpContextAccessor(); builder.Services.AddScoped<ICurrentUser, CurrentUser>(); builder.Services.AddScoped<CatalogService>();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => o.TokenValidationParameters = new() { ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), ValidateIssuer = false, ValidateAudience = false });
    builder.Services.AddAuthorization();
    var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
    Console.WriteLine("Isolated catalog HTTP server ready at http://127.0.0.1:5508");
    await app.RunAsync();
}
async Task<bool> Attempt()
{
    await using var context = new AppDbContext(options);
    try { await new CatalogService(context, new TestUser(adminId, "Administrator")).AddTariff(id, new() { Size = "45FT", Equipment = "DG", Amount = 50, EffectiveFrom = CatalogRules.Today }, default); return true; }
    catch (CatalogException e) when (e.StatusCode == 409) { return false; }
}
static void Check(bool valid, string message) { if (!valid) throw new Exception(message); Console.WriteLine("PASS: " + message); }
static async Task Expect<T>(int status, Func<Task<T>> run)
{
    try { await run(); } catch (CatalogException e) when (e.StatusCode == status) { Console.WriteLine("PASS: expected " + status + " " + e.ErrorCode); return; }
    throw new Exception("Expected catalog rejection " + status);
}
sealed class TestUser(Guid id, string role) : ICurrentUser
{
    public Guid? UserId => id; public string? Username => "catalog-test"; public string? Email => null; public string? Role => role; public bool IsAuthenticated => true;
}

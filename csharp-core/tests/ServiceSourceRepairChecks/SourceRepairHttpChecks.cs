using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;

static class SourceRepairHttpChecks
{
    private static readonly HttpClient Client = new() { BaseAddress = new Uri("http://127.0.0.1:5514/api/v1/ServiceDrafts/") };
    private static int checks;

    public static async Task Run(NpgsqlConnection pg)
    {
        var legacy = await SourceRepairFixtures.LegacySnapshot(pg);
        await Status(401, "GET", "sources?flow=EXPORT", user: 0);
        await Status(403, "GET", "sources?flow=EXPORT", user: 4, role: "Administrator");
        var broken = await Status(503, "GET", "sources?flow=EXPORT");
        Assert(S(broken, "errorCode") == "SERVICE_SOURCE_SCHEMA_MISMATCH", "Reproduces missing Booking company column as the reported source503.");
        var repair = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "source-repair.sql"));
        for (var pass = 0; pass < 2; pass++) await DraftFixtures.Sql(pg, repair);
        Assert(await SourceRepairFixtures.LegacySnapshot(pg) == legacy, "Applying migration038 twice preserves every original legacy Booking value.");
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM export_bookings WHERE carrier_id IS NOT NULL OR line_user_id IS NOT NULL OR request_id IS NOT NULL OR request_hash IS NOT NULL", pg))
            Assert((long)(await command.ExecuteScalarAsync())! == 0, "Repair does not infer company, shipping line or request ownership for old rows.");
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM nexusport_schema_versions WHERE version='20261109_038'", pg))
            Assert((long)(await command.ExecuteScalarAsync())! == 1, "Idempotent migration records exactly one038 receipt.");
        var repaired = await Status(200, "GET", "sources?flow=EXPORT");
        Assert(repaired["items"]!.AsArray().Count == 0, "Creator's current membership and shipper strings cannot reveal unassigned legacy Bookings.");
        await SourceRepairFixtures.InsertOwnedSources(pg);
        var sourceBefore = await DraftFixtures.Fingerprint(pg);
        await DraftFixtures.Sql(pg, repair);
        Assert(await DraftFixtures.Fingerprint(pg) == sourceBefore, "Reapplying038 also preserves already assigned canonical source rows.");
        var rejectsUnknownCompany = false;
        try { await DraftFixtures.Sql(pg, "INSERT INTO export_bookings(id,carrier_id) VALUES(@a,@b)", Guid.NewGuid(), Guid.NewGuid()); }
        catch (PostgresException error) when (error.SqlState == "23503") { rejectsUnknownCompany = true; }
        Assert(rejectsUnknownCompany, "Restored company relation enforces a real carrier foreign key.");
        var rejectsDuplicateRequest = false;
        try { await DraftFixtures.Sql(pg, "INSERT INTO export_bookings(id,carrier_id,request_id) VALUES(@a,@b,@c)", Guid.NewGuid(), DraftFixtures.Id(101), SourceRepairFixtures.Id(301)); }
        catch (PostgresException error) when (error.SqlState == "23505") { rejectsDuplicateRequest = true; }
        Assert(rejectsDuplicateRequest, "New company/request pairs cannot be duplicated after repair.");
        var sources = await Status(200, "GET", "sources?flow=EXPORT");
        Assert(sources["items"]!.AsArray().Count == 1 && G(sources["items"]![0]!, "id") == SourceRepairFixtures.Id(201), "Owned explicit sample Booking is selectable after schema repair.");
        Assert(sources["items"]![0]!["containerCount"]!.GetValue<int>() == 2, "The dropdown receives the two published full sample containers.");
        var spoofed = await Status(200, "GET", "sources?flow=EXPORT&carrierId=" + DraftFixtures.Id(102));
        Assert(G(spoofed["items"]![0]!, "id") == SourceRepairFixtures.Id(201), "Query-string company spoofing cannot change source access.");
        var other = await Status(200, "GET", "sources?flow=EXPORT", user: 2);
        Assert(other["items"]!.AsArray().Count == 1 && G(other["items"]![0]!, "id") == SourceRepairFixtures.Id(202), "The other company sees only its own explicit Booking.");
        await Status(404, "GET", $"containers?flow=EXPORT&sourceId={SourceRepairFixtures.Id(202)}");
        var containers = await Status(200, "GET", $"containers?flow=EXPORT&sourceId={SourceRepairFixtures.Id(201)}");
        Assert(containers["items"]!.AsArray().Count == 2, "A selected repaired Booking returns exact dry and reefer container occurrences.");
        var found = await Status(200, "GET", "sources?flow=EXPORT&search=REPAIR-BOOKING-201");
        Assert(found["items"]!.AsArray().Count == 1, "Booking search remains usable with persisted source data.");
        var missing = await Status(200, "GET", "sources?flow=EXPORT&search=no-such-booking");
        Assert(missing["items"]!.AsArray().Count == 0, "A search with no matching real Booking returns an empty list.");

        var draftId = Guid.NewGuid();
        var draftInput = new JsonObject
        {
            ["expectedVersion"] = 0, ["flow"] = "EXPORT", ["mainServiceId"] = DraftFixtures.Service(1).ToString(),
            ["source"] = new JsonObject { ["bookingId"] = SourceRepairFixtures.Id(201).ToString() },
            ["plannedAt"] = DateTimeOffset.UtcNow.AddHours(4).ToString("O"), ["contactName"] = "Repair verification", ["contactPhone"] = "0901234567",
            ["lines"] = new JsonArray(new JsonObject { ["sourceContainerId"] = SourceRepairFixtures.Id(301).ToString(), ["services"] = new JsonArray() }),
            ["carrierId"] = DraftFixtures.Id(102).ToString(), ["total"] = 1,
        };
        var draft = await Status(200, "PUT", draftId.ToString(), draftInput);
        Assert(S(draft, "status") == "DRAFT", "A repaired source can be saved as a real server-side draft.");
        await Status(404, "GET", draftId.ToString(), user: 2);
        var version = new JsonObject { ["expectedVersion"] = draft["version"]!.DeepClone() };
        var validation = await Status(200, "POST", $"{draftId}/validate", version);
        Assert(validation["canConfirm"]!.GetValue<bool>(), "Explicit company, valid source/VGM/seal and active tariff pass real confirmation checks.");
        Assert(validation["total"]!.GetValue<decimal>() == 300000m, "Fees use the real persisted tariff instead of accepting the client's total.");
        var confirmationInput = new JsonObject { ["expectedVersion"] = draft["version"]!.DeepClone(), ["quoteToken"] = validation["quoteToken"]!.DeepClone() };
        var confirmed = await Status(200, "POST", $"{draftId}/confirm", confirmationInput);
        Assert(S(confirmed, "status") == "CONFIRMED" && S(confirmed, "paymentStatus") == "UNPAID", "Source repair supports a valid confirmed order without granting payment or gate rights.");
        Assert(confirmed["total"]!.GetValue<decimal>() == 300000m, "Confirmation freezes the 300000 VND price.");
        var repeated = await Status(200, "POST", $"{draftId}/confirm", confirmationInput);
        Assert(G(repeated, "id") == G(confirmed, "id"), "Identical confirmation retries return the same persisted registration.");
        await Status(404, "GET", $"{draftId}/registration", user: 2);
        await Status(409, "PUT", draftId.ToString(), draftInput);
        Assert(await SourceRepairFixtures.LegacySnapshot(pg) == legacy, "Legacy Booking rows remain unchanged after the new source is used.");
        Assert(await DraftFixtures.Fingerprint(pg) == sourceBefore, "Draft/confirmation only read Booking, declaration, container and visit source rows.");
        Console.WriteLine($"PASS: {checks} guarded source-repair migration/HTTP/security/persistence checks. No shared database contacted.");
    }

    private static async Task<JsonNode> Status(int expected, string method, string path, JsonNode? body = null, int user = 1, string role = "Transport Company")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (user != 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user, role));
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == expected, $"HTTP {method} {path} expected {expected}, got {(int)response.StatusCode}." + ((int)response.StatusCode == expected ? "" : raw));
        return string.IsNullOrWhiteSpace(raw) ? new JsonObject() : JsonNode.Parse(raw) ?? new JsonObject();
    }
    private static string Token(int user, string role)
    {
        var claims = new[] { new Claim("sub", DraftFixtures.Id(user).ToString()), new Claim("id", DraftFixtures.Id(user).ToString()), new Claim("role", role) };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DraftFixtures.Secret)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(2), signingCredentials: credentials));
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS: " + message); }
    private static string S(JsonNode row, string key) => row[key]?.ToString() ?? "";
    private static Guid G(JsonNode row, string key) => Guid.Parse(S(row, key));
}

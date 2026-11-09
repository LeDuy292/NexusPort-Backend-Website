using Npgsql;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

static class DraftHttpChecks
{
    private static readonly HttpClient Client = new() { BaseAddress = new Uri("http://127.0.0.1:5510/api/v1/ServiceDrafts/") };
    private static int checks;

    public static async Task Run(NpgsqlConnection pg)
    {
        var before = await DraftFixtures.Fingerprint(pg);
        await Status(401, "GET", "", null, 0);
        await Status(403, "GET", "", null, 4, "Administrator");
        await Status(403, "GET", "", null, 7, "Shipping Line");
        await Status(403, "GET", "", null, 5);
        await Status(403, "GET", "", null, 6);
        await Status(403, "GET", "", null, 1, "Carrier Staff");
        await Status(200, "GET", "sources?flow=EXPORT", null, 3, "Carrier Staff");

        var sources = await Status(200, "GET", "sources?flow=EXPORT");
        var sourceRows = sources.Json["items"]!.AsArray();
        Assert(sourceRows.Count == 1 && Id(sourceRows[0]!, "id") == DraftFixtures.Id(201), "Source list excludes other companies and cancelled bookings.");
        Assert(sourceRows[0]!["containerCount"]!.GetValue<int>() == 2, "Published export source count excludes empty containers.");
        var exportContainers = await Status(200, "GET", $"containers?flow=EXPORT&sourceId={DraftFixtures.Id(201)}");
        Assert(exportContainers.Json["items"]!.AsArray().Count == 2, "Export picker returns exactly the two full published occurrences.");
        Assert(exportContainers.Json["items"]!.AsArray().All(row => row!["loadStatus"]!.ToString() == "full"), "Empty containers are not registration candidates.");
        var search = await Status(200, "GET", $"containers?flow=EXPORT&sourceId={DraftFixtures.Id(201)}&search=REEF");
        Assert(search.Json["items"]!.AsArray().Count == 1 && Id(search.Json["items"]![0]!, "sourceContainerId") == DraftFixtures.Id(304), "Container search keeps the exact operational occurrence.");
        await Status(404, "GET", $"containers?flow=EXPORT&sourceId={DraftFixtures.Id(202)}");
        await Status(422, "GET", $"containers?flow=EXPORT&sourceId={DraftFixtures.Id(203)}");
        await Status(422, "GET", "containers?flow=EXPORT");
        await Status(422, "GET", "sources?flow=EMPTY");
        await Status(422, "GET", "?pageSize=51");

        var importSources = await Status(200, "GET", "sources?flow=IMPORT");
        Assert(importSources.Json["referenceMode"]!.ToString() == "MANUAL" && importSources.Json["items"]!.AsArray().Count == 0, "Import does not invent a verified eDO source.");
        var importContainers = await Status(200, "GET", "containers?flow=IMPORT");
        Assert(importContainers.Json["items"]!.AsArray().Count == 2, "Import list contains owned and explicitly access-granted full visits only.");
        var shared = importContainers.Json["items"]!.AsArray().Single(row => Id(row!, "sourceContainerId") == DraftFixtures.Id(403))!;
        Assert(shared["cargoType"] == null && shared["sealNumber"] == null, "Shared foreign visits do not disclose source-company cargo or seal.");
        Assert(!importContainers.Raw.Contains("SECRET", StringComparison.Ordinal), "Import picker never returns pickup codes, visit secrets or private cargo.");
        await Status(422, "GET", $"containers?flow=IMPORT&sourceId={DraftFixtures.Id(201)}");

        var draftId = Guid.NewGuid();
        var payload = Export();
        payload["carrierId"] = DraftFixtures.Id(102).ToString();
        payload["status"] = "APPROVED";
        payload["containers"] = new JsonArray(new JsonObject { ["containerNumber"] = "CLIENT_OVERRIDE", ["sealNumber"] = "CLIENT_OVERRIDE" });
        var saved = await Status(200, "PUT", draftId.ToString(), payload);
        Assert(saved.Json["status"]!.ToString() == "DRAFT" && saved.Json["version"]!.GetValue<int>() == 1, "Only DRAFT is persisted, regardless of client-supplied status.");
        Assert(saved.Json["containers"]![0]!["containerNumber"]!.ToString() == "DRYU1000001" && !saved.Raw.Contains("CLIENT_OVERRIDE", StringComparison.Ordinal), "Container snapshots are resolved by the server.");
        Assert(saved.Json["lines"]![0]!["services"]!.AsArray().Count(row => Id(row!, "serviceId") == DraftFixtures.Service(1)) == 1, "Main service is included exactly once on each selected container.");
        Assert(DateTimeOffset.Parse(saved.Json["plannedAt"]!.ToString()).UtcDateTime.Hour == 3, "Local +07:00 input retains its instant in UTC.");
        Assert(saved.Json["sourceSnapshot"]!["verificationStatus"]!.ToString() == "SOURCE_LINKED", "Export draft links its source without claiming gate authorization.");
        var replay = await Status(200, "PUT", draftId.ToString(), payload);
        Assert(replay.Json["version"]!.GetValue<int>() == 1 && Id(replay.Json, "id") == draftId, "Retrying the initial identical save does not create another draft/version.");
        var changedReplay = Copy(payload); changedReplay["note"] = "Different initial retry";
        await Status(409, "PUT", draftId.ToString(), changedReplay);
        await Status(200, "GET", draftId.ToString(), null, 3, "Carrier Staff");
        await Status(404, "GET", draftId.ToString(), null, 2);
        await Status(404, "PUT", draftId.ToString(), payload, 2);
        var otherList = await Status(200, "GET", "", null, 2);
        Assert(otherList.Json["items"]!.AsArray().All(row => Id(row!, "id") != draftId), "A spoofed carrier ID cannot move the draft into another tenant.");

        var updatedPayload = Copy(payload); updatedPayload["expectedVersion"] = 1; updatedPayload["contactName"] = "Updated customer contact";
        var updated = await Status(200, "PUT", draftId.ToString(), updatedPayload);
        Assert(updated.Json["version"]!.GetValue<int>() == 2 && updated.Json["contactName"]!.ToString() == "Updated customer contact", "Saved drafts reopen and update with optimistic versioning.");
        await Status(409, "PUT", draftId.ToString(), updatedPayload);
        var first = Copy(updatedPayload); first["expectedVersion"] = 2; first["note"] = "Concurrent first";
        var second = Copy(first); second["note"] = "Concurrent second";
        var concurrent = await Task.WhenAll(Request("PUT", draftId.ToString(), first), Request("PUT", draftId.ToString(), second));
        Assert(concurrent.Count(result => result.Status == 200) == 1 && concurrent.Count(result => result.Status == 409) == 1, "Concurrent edits cannot overwrite each other.");

        var incomplete = new JsonObject { ["flow"] = "EXPORT", ["note"] = "Preparation not finished" };
        var partial = await Status(200, "PUT", Guid.NewGuid().ToString(), incomplete);
        Assert(partial.Json["mainServiceId"] == null && partial.Json["lines"]!.AsArray().Count == 0, "Incomplete preparation can be saved and resumed as a draft.");
        var badSource = Export(); badSource["source"]!["bookingId"] = DraftFixtures.Id(202).ToString();
        await Status(404, "PUT", Guid.NewGuid().ToString(), badSource);
        var wrongOccurrence = Export(); wrongOccurrence["lines"]![0]!["sourceContainerId"] = DraftFixtures.Id(302).ToString();
        await Status(422, "PUT", Guid.NewGuid().ToString(), wrongOccurrence);
        var physicalId = Export(); physicalId["lines"]![0]!["sourceContainerId"] = DraftFixtures.Id(501).ToString();
        await Status(422, "PUT", Guid.NewGuid().ToString(), physicalId);
        var empty = Export(); empty["lines"]![0]!["sourceContainerId"] = DraftFixtures.Id(303).ToString();
        await Status(422, "PUT", Guid.NewGuid().ToString(), empty);
        var duplicates = Export(); duplicates["lines"]!.AsArray().Add(duplicates["lines"]![0]!.DeepClone());
        await Status(422, "PUT", Guid.NewGuid().ToString(), duplicates);
        var invalidTime = Export(); invalidTime["plannedAt"] = "2026-11-08T10:00:00";
        await Status(422, "PUT", Guid.NewGuid().ToString(), invalidTime);
        var reversed = Export(); reversed["lines"]![0]!["services"] = new JsonArray(Usage(4, "2026-11-08T12:00:00+07:00", "2026-11-08T11:00:00+07:00"));
        await Status(422, "PUT", Guid.NewGuid().ToString(), reversed);
        var suspended = Export(); suspended["mainServiceId"] = DraftFixtures.Service(6).ToString();
        await Status(422, "PUT", Guid.NewGuid().ToString(), suspended);
        var wrongFlow = Export(); wrongFlow["mainServiceId"] = DraftFixtures.Service(2).ToString();
        await Status(422, "PUT", Guid.NewGuid().ToString(), wrongFlow);
        var dryPower = Export(); dryPower["lines"]![0]!["services"] = new JsonArray(Usage(3));
        await Status(422, "PUT", Guid.NewGuid().ToString(), dryPower);
        var decimalMove = Export(); decimalMove["lines"]![0]!["services"] = new JsonArray(new JsonObject { ["serviceId"] = DraftFixtures.Service(4).ToString(), ["quantity"] = 1.25 });
        await Status(422, "PUT", Guid.NewGuid().ToString(), decimalMove);
        var tooLong = Export(); tooLong["note"] = new string('x', 1001);
        await Status(422, "PUT", Guid.NewGuid().ToString(), tooLong);

        var reefer = Export(); reefer["lines"]![0]!["sourceContainerId"] = DraftFixtures.Id(304).ToString();
        reefer["lines"]![0]!["services"] = new JsonArray(Usage(3, "2026-11-08T10:00:00+07:00", "2026-11-08T13:30:00+07:00"));
        var reeferSaved = await Status(200, "PUT", Guid.NewGuid().ToString(), reefer);
        Assert(reeferSaved.Json["lines"]![0]!["services"]!.AsArray().Count == 2, "Reefer usage dates are retained along with the main service, without fabricating billing quantity.");
        var import = Import();
        var importSaved = await Status(200, "PUT", Guid.NewGuid().ToString(), import);
        Assert(importSaved.Json["sourceSnapshot"]!["verificationStatus"]!.ToString() == "UNVERIFIED" && importSaved.Json["status"]!.ToString() == "DRAFT", "A declared eDO reference remains unverified and cannot authorize receipt/gate passage.");
        Assert(importSaved.Json["source"]!["referenceNumber"]!.ToString() == "EDO-TEST-ONLY", "Customer's eDO reference can be resumed.");
        var privateImport = Import(); privateImport["lines"]![0]!["sourceContainerId"] = DraftFixtures.Id(402).ToString();
        await Status(422, "PUT", Guid.NewGuid().ToString(), privateImport);
        var sharedImport = Import(); sharedImport["lines"]![0]!["sourceContainerId"] = DraftFixtures.Id(403).ToString();
        var sharedSaved = await Status(200, "PUT", Guid.NewGuid().ToString(), sharedImport);
        Assert(!sharedSaved.Raw.Contains("SECRET", StringComparison.Ordinal), "Persisted shared-visit snapshots preserve cargo privacy.");
        var found = await Status(200, "GET", "?search=" + Uri.EscapeDataString(saved.Json["number"]!.ToString()));
        Assert(found.Json["items"]!.AsArray().Count == 1 && Id(found.Json["items"]![0]!, "id") == draftId, "Draft list supports reopening a specific saved reference.");
        await DraftFixtures.Sql(pg, "ALTER TABLE export_bookings RENAME COLUMN carrier_id TO unavailable_company_id");
        try
        {
            var incompatible = await Status(503, "GET", "sources?flow=EXPORT");
            Assert(incompatible.Json["errorCode"]!.ToString() == "SERVICE_SOURCE_SCHEMA_MISMATCH",
                "A missing Booking company relation reports a source mismatch instead of falsely requesting migration 037.");
        }
        finally { await DraftFixtures.Sql(pg, "ALTER TABLE export_bookings RENAME COLUMN unavailable_company_id TO carrier_id"); }
        Assert(await DraftFixtures.Fingerprint(pg) == before, "All Booking, declaration, physical container, visit and access rows remain unchanged.");
        Console.WriteLine($"PASS: {checks} guarded service-draft HTTP/security/persistence checks. No shared database contacted.");
    }

    private static JsonObject Export() => new()
    {
        ["expectedVersion"] = 0, ["flow"] = "EXPORT", ["mainServiceId"] = DraftFixtures.Service(1).ToString(),
        ["source"] = new JsonObject { ["bookingId"] = DraftFixtures.Id(201).ToString() },
        ["plannedAt"] = "2026-11-08T10:00:00+07:00", ["contactName"] = "Draft customer contact", ["contactPhone"] = "0901234567", ["note"] = "Isolated test draft",
        ["lines"] = new JsonArray(new JsonObject { ["sourceContainerId"] = DraftFixtures.Id(301).ToString(), ["services"] = new JsonArray() })
    };
    private static JsonObject Import() => new()
    {
        ["expectedVersion"] = 0, ["flow"] = "IMPORT", ["mainServiceId"] = DraftFixtures.Service(2).ToString(),
        ["source"] = new JsonObject { ["documentType"] = "EDO", ["referenceNumber"] = "EDO-TEST-ONLY", ["blNumber"] = "BL-TEST-ONLY", ["validUntil"] = "2026-11-20" },
        ["lines"] = new JsonArray(new JsonObject { ["sourceContainerId"] = DraftFixtures.Id(401).ToString(), ["services"] = new JsonArray() })
    };
    private static JsonObject Usage(int service, string? from = null, string? to = null) => new()
    { ["serviceId"] = DraftFixtures.Service(service).ToString(), ["startsAt"] = from, ["endsAt"] = to };
    private static JsonObject Copy(JsonObject node) => node.DeepClone().AsObject();
    private static Guid Id(JsonNode node, string key) => Guid.Parse(node[key]!.ToString());
    private static void Assert(bool valid, string message)
    { if (!valid) throw new Exception(message); checks++; Console.WriteLine("PASS: " + message); }

    private static async Task<Reply> Status(int expected, string method, string path, JsonNode? body = null, int user = 1, string role = "Transport Company")
    {
        var reply = await Request(method, path, body, user, role);
        Assert(reply.Status == expected, $"HTTP {method} {(path.Length > 90 ? path[..90] : path)} expected {expected}, got {reply.Status}" + (reply.Status != expected ? ": " + reply.Raw : "."));
        return reply;
    }
    private static async Task<Reply> Request(string method, string path, JsonNode? body = null, int user = 1, string role = "Transport Company")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (user != 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user, role));
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        JsonNode parsed;
        try { parsed = JsonNode.Parse(raw) ?? new JsonObject(); } catch { parsed = new JsonObject(); }
        return new((int)response.StatusCode, parsed, raw);
    }
    private static string Token(int user, string role)
    {
        var claims = new[] { new Claim("sub", DraftFixtures.Id(user).ToString()), new Claim("id", DraftFixtures.Id(user).ToString()), new Claim("role", role), new Claim("name", "Draft fixture") };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DraftFixtures.Secret)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(2), signingCredentials: credentials));
    }
    private sealed record Reply(int Status, JsonNode Json, string Raw);
}

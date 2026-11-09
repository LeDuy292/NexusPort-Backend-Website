using Npgsql;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

static class RegistrationHttpChecks
{
    private static readonly HttpClient Client = new() { BaseAddress = new Uri("http://127.0.0.1:5512/api/v1/ServiceDrafts/") };
    private static int checks;
    private static readonly DateTimeOffset Planned = DateTimeOffset.UtcNow.AddHours(3);

    public static async Task Run(NpgsqlConnection pg)
    {
        var before = await DraftFixtures.Fingerprint(pg);
        var initial = await Save(Export());
        var draft = G(initial, "id");
        await Status(401, "POST", $"{draft}/validate", Version(initial), 0);
        await Status(403, "POST", $"{draft}/validate", Version(initial), 4, "Administrator");
        await Status(403, "POST", $"{draft}/validate", Version(initial), 7, "Shipping Line");
        await Status(403, "POST", $"{draft}/validate", Version(initial), 5);
        await Status(403, "POST", $"{draft}/validate", Version(initial), 6);
        await Status(403, "POST", $"{draft}/validate", Version(initial), 1, "Carrier Staff");
        await Status(404, "POST", $"{draft}/validate", Version(initial), 2);
        await Status(404, "POST", $"{draft}/confirm", new JsonObject { ["expectedVersion"] = 1, ["quoteToken"] = new string('F', 64) }, 2);
        await Status(404, "GET", $"{draft}/registration", null, 2);
        await Status(404, "POST", $"{Guid.NewGuid()}/validate", new JsonObject { ["expectedVersion"] = 1 });
        await Status(409, "POST", $"{draft}/validate", new JsonObject { ["expectedVersion"] = 999 });

        var quote = await Validate(initial);
        Assert(B(quote, "canConfirm"), "Complete owned export registration can be confirmed.");
        Assert(quote["quoteToken"] is JsonValue && !string.IsNullOrEmpty(S(quote, "quoteToken")), "Preview returns a server-generated confirmation token.");
        Assert(D(quote, "total") == 300000m && D(quote, "subtotal") == 300000m, "Exact size/equipment tariff wins, and all amount arithmetic is server-generated.");
        Assert(Fee(quote, 1)["tariffId"]!.ToString() == RegistrationFixtures.Id(904).ToString(), "Fee keeps the exact matched tariff identity.");
        Assert(D(Fee(quote, 1), "quantity") == 1m && D(Fee(quote, 1), "unitPrice") == 300000m, "Container service always charges one container.");
        Assert(Issues(quote).Any(issue => S(issue, "severity") == "WARNING"), "Customs release still pending is shown without falsely granting gate permission.");
        var staff = await Status(200, "POST", $"{draft}/validate", Version(initial), 3, "Carrier Staff");
        Assert(B(staff.Json, "canConfirm"), "Another active employee in the same company can review the draft.");
        await Status(200, "POST", $"{draft}/validate", Version(initial), 8, "Carrier");
        var spoofedReview = Version(initial); spoofedReview["carrierId"] = DraftFixtures.Id(102).ToString(); spoofedReview["unitPrice"] = 1; spoofedReview["total"] = 1;
        var spoof = await Status(200, "POST", $"{draft}/validate", spoofedReview);
        Assert(D(spoof.Json, "total") == 300000m && S(spoof.Json, "quoteToken") == S(quote, "quoteToken"), "Client company/price/total fields cannot change the validated quote.");

        var confirmInput = ConfirmInput(initial, quote);
        confirmInput["total"] = 1; confirmInput["paymentStatus"] = "PAID"; confirmInput["status"] = "COMPLETED";
        var confirmed = (await Status(200, "POST", $"{draft}/confirm", confirmInput)).Json;
        Assert(S(confirmed, "status") == "CONFIRMED" && S(confirmed, "paymentStatus") == "UNPAID", "Confirmed services remain unpaid and never accept a client-forged paid/completed status.");
        Assert(D(confirmed, "total") == 300000m && D(Fee(confirmed, 1), "unitPrice") == 300000m, "Confirmation freezes the accepted preview unit price and amount.");
        Assert(confirmed["confirmedAt"] != null && confirmed["id"] != null, "Confirmed order has a persistent identity and confirmation timestamp.");
        var replay = (await Status(200, "POST", $"{draft}/confirm", ConfirmInput(initial, quote))).Json;
        Assert(G(replay, "id") == G(confirmed, "id"), "An identical confirmation retry returns the existing registration.");
        var tampered = ConfirmInput(initial, quote); tampered["quoteToken"] = new string('A', 64);
        await Status(409, "POST", $"{draft}/confirm", tampered);
        await Status(404, "GET", $"{draft}/registration", null, 2);
        var frozen = (await Status(200, "GET", $"{draft}/registration")).Json;
        Assert(G(frozen, "id") == G(confirmed, "id") && D(frozen, "total") == 300000m, "Company can reopen its confirmed price snapshot.");
        var listed = (await Status(200, "GET", "?search=" + Uri.EscapeDataString(S(confirmed, "number")))).Json["items"]!.AsArray();
        Assert(listed.Count == 1 && G(listed[0]!, "id") == draft && S(listed[0]!, "registrationNumber") == S(confirmed, "number"),
            "Customer can find a confirmed order by the registration number displayed in the UI.");
        Assert(DateTimeOffset.Parse(S(listed[0]!, "updatedAt")) == DateTimeOffset.Parse(S(confirmed, "confirmedAt")),
            "Confirmed list date reflects confirmation instead of the older draft save date.");
        var rejectedMutation = false;
        try { await DraftFixtures.Sql(pg, "UPDATE eport_service_registrations SET total=1 WHERE draft_id=@a", draft); }
        catch (PostgresException error) when (error.SqlState == "23514") { rejectedMutation = true; }
        Assert(rejectedMutation, "The database itself prevents overwriting a confirmed financial snapshot.");
        var edited = Export(); edited["expectedVersion"] = 1; edited["note"] = "Attempt to change a confirmed registration";
        await Status(409, "PUT", draft.ToString(), edited);
        var createReplay = Export();
        await Status(409, "PUT", draft.ToString(), createReplay);

        await WithChanged(pg, "eport_tariffs", "is_active", RegistrationFixtures.Id(904), false, async () =>
        {
            var historical = (await Status(200, "GET", $"{draft}/registration")).Json;
            Assert(D(Fee(historical, 1), "unitPrice") == 300000m && D(historical, "total") == 300000m, "Retiring a tariff does not change historical confirmed prices.");
        });
        await WithChanged(pg, "eport_services", "name", DraftFixtures.Service(1), "A changed current service name", async () =>
        {
            var historical = (await Status(200, "GET", $"{draft}/registration")).Json;
            Assert(!historical.ToJsonString().Contains("A changed current service name", StringComparison.Ordinal), "Historical registration keeps service description/name snapshots.");
            var summary = (await Status(200, "GET", "?search=" + Uri.EscapeDataString(S(confirmed, "number")))).Json["items"]![0]!;
            Assert(S(summary, "mainServiceName") == S(quote, "mainServiceName"), "Confirmed list keeps the accepted service name after catalog edits.");
            var searchFrozenName = (await Status(200, "GET", "?pageSize=50&search=" + Uri.EscapeDataString(S(quote, "mainServiceName")))).Json["items"]!.AsArray();
            Assert(searchFrozenName.Any(row => G(row!, "id") == draft), "Confirmed list search matches the frozen service name shown to the customer.");
        });
        await WithChanged(pg, "export_bookings", "booking_no", DraftFixtures.Id(201), "CHANGED-LIVE-BOOKING-NUMBER", async () =>
        {
            var summary = (await Status(200, "GET", "?search=" + Uri.EscapeDataString(S(confirmed, "number")))).Json["items"]![0]!;
            Assert(S(summary, "sourceLabel") == "TEST-EXPORT-201", "Confirmed list keeps the source Booking label accepted at confirmation.");
        });

        var partial = await Save(new JsonObject { ["flow"] = "EXPORT", ["note"] = "Incomplete fixture" });
        var missing = await Validate(partial);
        Assert(!B(missing, "canConfirm") && Issues(missing).Count(issue => S(issue, "severity") == "ERROR") >= 3, "Missing source/container/service/contact information is aggregated instead of reporting only one field.");
        Assert(missing["total"] == null, "An incomplete registration is not displayed as a free valid order.");
        await Status(422, "POST", $"{G(partial, "id")}/confirm", ConfirmInput(partial, missing));

        var noRate = await Save(Export());
        var beforeRateChange = await Validate(noRate);
        await WithChanged(pg, "eport_tariffs", "amount", RegistrationFixtures.Id(904), 310000m, async () =>
        {
            await Status(409, "POST", $"{G(noRate, "id")}/confirm", ConfirmInput(noRate, beforeRateChange));
            var currentPrice = await Validate(noRate);
            Assert(D(currentPrice, "total") == 310000m && S(currentPrice, "quoteToken") != S(beforeRateChange, "quoteToken"), "Changed live price requires review of a new quote, never silent repricing on confirm.");
        });

        var sourceChanged = await Save(Export());
        var sourceQuote = await Validate(sourceChanged);
        await WithChanged(pg, "export_booking_containers", "seal_number", DraftFixtures.Id(301), "NEW-SOURCE-SEAL", async () =>
        {
            await Status(409, "POST", $"{G(sourceChanged, "id")}/confirm", ConfirmInput(sourceChanged, sourceQuote));
            Assert(S(await Validate(sourceChanged), "quoteToken") != S(sourceQuote, "quoteToken"), "Source metadata changes invalidate the accepted preview.");
        });

        var versionChanged = await Save(Export());
        var oldQuote = await Validate(versionChanged);
        var updatedInput = Export(); updatedInput["expectedVersion"] = 1; updatedInput["note"] = "New draft content";
        var currentDraft = (await Status(200, "PUT", G(versionChanged, "id").ToString(), updatedInput)).Json;
        await Status(409, "POST", $"{G(versionChanged, "id")}/confirm", ConfirmInput(versionChanged, oldQuote));
        Assert((await Validate(currentDraft))["quoteToken"]!.ToString() != S(oldQuote, "quoteToken"), "Draft changes invalidate previous review tokens.");

        var concurrencyDraft = await Save(Export());
        var concurrencyQuote = await Validate(concurrencyDraft);
        var concurrent = await Task.WhenAll(
            Request("POST", $"{G(concurrencyDraft, "id")}/confirm", ConfirmInput(concurrencyDraft, concurrencyQuote)),
            Request("POST", $"{G(concurrencyDraft, "id")}/confirm", ConfirmInput(concurrencyDraft, concurrencyQuote)));
        Assert(concurrent.All(reply => reply.Status == 200) && concurrent.Select(reply => G(reply.Json, "id")).Distinct().Count() == 1, "Concurrent identical confirmations create exactly one registration and both callers receive its receipt.");
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM eport_service_registrations WHERE draft_id=@a", pg))
        {
            count.Parameters.AddWithValue("a", G(concurrencyDraft, "id"));
            Assert((long)(await count.ExecuteScalarAsync())! == 1, "Database uniqueness protects confirmation from duplicate orders.");
        }

        await ValidationFailures(pg);
        await FeePolicies(pg);
        Assert(await DraftFixtures.Fingerprint(pg) == before, "Registration validation/confirmation never modifies Booking, containers, declarations, visits or company access.");
        Console.WriteLine($"PASS: {checks} guarded service-registration HTTP/security/pricing/persistence checks. No shared database contacted.");
    }

    private static async Task ValidationFailures(NpgsqlConnection pg)
    {
        var candidate = await Save(Export());
        foreach (var (table, column, id, value, label) in new (string, string, Guid, object, string)[]
        {
            ("containers","status",DraftFixtures.Id(501),"CustomsHold","Held physical container blocks confirmation."),
            ("containers","status",DraftFixtures.Id(501),"blocked","Blocked physical container blocks confirmation."),
            ("export_bookings","port_status",DraftFixtures.Id(201),"Pending","Booking acceptance is rechecked instead of trusting draft snapshot."),
            ("export_booking_containers","active",DraftFixtures.Id(301),false,"Removed source occurrences cannot be confirmed."),
            ("export_booking_containers","vgm_kg",DraftFixtures.Id(301),0m,"Nonpositive source VGM blocks export confirmation."),
            ("export_booking_containers","seal_number",DraftFixtures.Id(301),"","Missing source seal is reported."),
            ("export_voyages","status",DraftFixtures.Id(701),"Closed","A closed voyage invalidates new service confirmation."),
            ("export_voyages","cy_cutoff",DraftFixtures.Id(701),DateTime.UtcNow.AddHours(-1),"Expired CY window blocks confirmation."),
            ("eport_services","is_active",DraftFixtures.Service(1),false,"Suspended service invalidates current registration."),
        }) await WithChanged(pg, table, column, id, value, async () =>
        {
            var result = await Validate(candidate);
            Assert(!B(result, "canConfirm") && Issues(result).Any(issue => S(issue, "severity") == "ERROR"), label);
        });
        var outsideWindow = Export(); outsideWindow["plannedAt"] = DateTimeOffset.UtcNow.AddDays(15).ToString("O");
        Assert(!B(await Validate(await Save(outsideWindow)), "canConfirm"), "Planned service time outside the source receiving window is invalid.");
        var past = Export(); past["plannedAt"] = DateTimeOffset.UtcNow.AddHours(-2).ToString("O");
        Assert(!B(await Validate(await Save(past)), "canConfirm"), "New confirmations cannot schedule services in the past.");

        var import = await Save(Import());
        var imported = await Validate(import);
        Assert(!B(imported, "canConfirm") && Issues(imported).Any(issue => S(issue, "code") == "EDO_UNVERIFIED"), "A manually declared eDO never becomes authoritative receive permission.");
        await Status(422, "POST", $"{G(import, "id")}/confirm", ConfirmInput(import, imported));
        Assert(!imported.ToJsonString().Contains("SECRET", StringComparison.Ordinal), "Registration review does not disclose pickup codes or legacy private data.");

        var row = await Save(Export());
        await WithChanged(pg, "users", "is_active", DraftFixtures.Id(1), false, async () =>
            await Status(403, "POST", $"{G(row, "id")}/validate", Version(row)));
    }

    private static async Task FeePolicies(NpgsqlConnection pg)
    {
        var general = await Save(Export());
        await WithChanged(pg, "eport_tariffs", "is_active", RegistrationFixtures.Id(904), false, async () =>
        {
            var fallback = await Validate(general);
            Assert(D(Fee(fallback, 1), "unitPrice") == 200000m, "An exact size tariff outranks equipment-only and general fallbacks.");
            await WithChanged(pg, "eport_tariffs", "is_active", RegistrationFixtures.Id(902), false, async () =>
            {
                Assert(D(Fee(await Validate(general), 1), "unitPrice") == 100000m, "Equipment-specific tariff outranks the general fallback.");
                await WithChanged(pg, "eport_tariffs", "is_active", RegistrationFixtures.Id(903), false, async () =>
                {
                    Assert(D(Fee(await Validate(general), 1), "unitPrice") == 90000m, "General tariff applies only when more specific tariffs are unavailable.");
                    await WithChanged(pg, "eport_tariffs", "is_active", RegistrationFixtures.Id(901), false, async () =>
                    {
                        var missing = await Validate(general);
                        Assert(!B(missing, "canConfirm") && missing["total"] == null, "No matching active price leaves total unknown and blocks confirmation.");
                    });
                });
            });
        });

        await WithChanged(pg, "eport_tariffs", "amount", RegistrationFixtures.Id(904), 0m, async () =>
        {
            var free = await Save(Export()); var quote = await Validate(free);
            Assert(B(quote, "canConfirm") && D(quote, "total") == 0m, "An explicitly configured zero tariff is distinct from missing price.");
            var confirmed = (await Status(200, "POST", $"{G(free, "id")}/confirm", ConfirmInput(free, quote))).Json;
            Assert(S(confirmed, "paymentStatus") == "NOT_REQUIRED", "Zero-priced confirmed registration clearly requires no payment.");
        });

        var timed = Export(true);
        var timedDraft = await Save(timed);
        var quoteTimed = await Validate(timedDraft);
        Assert(B(quoteTimed, "canConfirm"), "Planned export add-ons can be registered with explicit pending physical prerequisites.");
        Assert(D(Fee(quoteTimed, 3), "quantity") == 1.01m && D(Fee(quoteTimed, 3), "amount") == 1111m, "Hourly duration rounds upward to hundredths and the VND line is calculated exactly.");
        Assert(D(Fee(quoteTimed, 5), "quantity") == 2m && D(Fee(quoteTimed, 5), "amount") == 14000m, "Storage duration rounds upward by elapsed 24-hour units.");
        var timedConfirmed = (await Status(200, "POST", $"{G(timedDraft, "id")}/confirm", ConfirmInput(timedDraft, quoteTimed))).Json;
        Assert(S(timedConfirmed, "status") == "PENDING_APPROVAL" && D(timedConfirmed, "total") == 515111m, "Yard-dependent services freeze their full fee breakdown while clearly awaiting approval.");

        var earlyAddon = Export(true);
        earlyAddon["lines"]![0]!["services"]![0]!["startsAt"] = Planned.AddHours(-1).ToString("O");
        Assert(!B(await Validate(await Save(earlyAddon)), "canConfirm"), "A yard add-on cannot start before planned export arrival.");
        var missingTime = Export(true);
        missingTime["lines"]![0]!["services"]![0]!["endsAt"] = null;
        Assert(!B(await Validate(await Save(missingTime)), "canConfirm"), "Time-based fee lines require both usage boundaries.");
        var containerQuantity = Export();
        containerQuantity["lines"]![0]!["services"] = new JsonArray(new JsonObject { ["serviceId"] = DraftFixtures.Service(1).ToString(), ["quantity"] = 9 });
        Assert(D(Fee(await Validate(await Save(containerQuantity)), 1), "quantity") == 1m, "A client-provided quantity cannot multiply one per-container charge.");

        var moves = Export();
        moves["lines"]![0]!["services"] = new JsonArray(new JsonObject { ["serviceId"] = DraftFixtures.Service(4).ToString(), ["quantity"] = 2 });
        var moveQuote = await Validate(await Save(moves));
        Assert(D(Fee(moveQuote, 4), "quantity") == 2m && D(Fee(moveQuote, 4), "amount") == 40000m, "Movement services use the requested positive integer number of moves.");
        var missingMoveQuantity = Export();
        missingMoveQuantity["lines"]![0]!["services"] = new JsonArray(new JsonObject { ["serviceId"] = DraftFixtures.Service(4).ToString() });
        Assert(!B(await Validate(await Save(missingMoveQuantity)), "canConfirm"), "Move/weigh quantity must be declared rather than guessed during confirmation.");

        await WithChanged(pg, "eport_tariffs", "amount", RegistrationFixtures.Id(907), 999999999901m, async () =>
        {
            var oversized = Export(true);
            oversized["lines"]![0]!["services"] = new JsonArray(Usage(3, Planned.AddHours(1), Planned.AddHours(1).AddHours(9999.97)));
            var rejected = await Validate(await Save(oversized));
            Assert(!B(rejected, "canConfirm") && rejected["quoteToken"] == null && rejected["total"] == null,
                "An amount above JavaScript safe integer range cannot be confirmed or presented as an exact total.");
            Assert(Fee(rejected, 3)["amount"] == null && Issues(rejected).Any(issue => S(issue, "code") == "AMOUNT_TOO_LARGE"),
                "Oversized fee line has an explicit amount-limit error instead of losing currency precision.");
        });

        var scheduledDate = Planned.AddDays(2);
        var priceDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(scheduledDate, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")).DateTime);
        await DraftFixtures.Sql(pg, """
            INSERT INTO eport_tariffs(id,service_id,size,equipment,amount,unit,effective_from,effective_to,note,created_by)
            VALUES(@a,@b,'20FT','DRY',333000,'CONTAINER',@c,NULL,'Future dated isolated fixture',@d)
            ON CONFLICT(id) DO UPDATE SET is_active=true,effective_from=excluded.effective_from,effective_to=NULL
            """, RegistrationFixtures.Id(910), DraftFixtures.Service(1), priceDate, DraftFixtures.Id(4));
        try
        {
            await WithChanged(pg, "eport_tariffs", "effective_to", RegistrationFixtures.Id(904), priceDate.AddDays(-1), async () =>
            {
                var future = Export(); future["plannedAt"] = scheduledDate.ToString("O");
                Assert(D(Fee(await Validate(await Save(future)), 1), "unitPrice") == 333000m, "Fee selects tariff on the planned usage date in UTC+7, including future-dated prices.");
                Assert(D(Fee(await Validate(await Save(Export())), 1), "unitPrice") == 300000m, "Future tariff is not applied early to a service scheduled before its start date.");
                var forgedTime = Export();
                forgedTime["lines"]![0]!["services"] = new JsonArray(Usage(1, scheduledDate, scheduledDate.AddHours(1)));
                Assert(D(Fee(await Validate(await Save(forgedTime)), 1), "unitPrice") == 300000m, "Non-time service usage fields cannot override the validated planned date to select another tariff.");
            });
        }
        finally { await DraftFixtures.Sql(pg, "UPDATE eport_tariffs SET is_active=false WHERE id=@a", RegistrationFixtures.Id(910)); }
    }

    public static JsonObject Export(bool addons = false) => new()
    {
        ["expectedVersion"] = 0, ["flow"] = "EXPORT", ["mainServiceId"] = DraftFixtures.Service(1).ToString(),
        ["source"] = new JsonObject { ["bookingId"] = DraftFixtures.Id(201).ToString() },
        ["plannedAt"] = Planned.ToString("O"), ["contactName"] = "Registration fixture contact", ["contactPhone"] = "0901234567", ["note"] = "Isolated registration check",
        ["lines"] = new JsonArray(new JsonObject
        {
            ["sourceContainerId"] = DraftFixtures.Id(addons ? 304 : 301).ToString(),
            ["services"] = addons ? new JsonArray(
                Usage(3, Planned.AddHours(1), Planned.AddHours(2).AddSeconds(1)),
                Usage(5, Planned.AddHours(1), Planned.AddHours(25).AddSeconds(1))) : new JsonArray()
        })
    };
    private static JsonObject Import() => new()
    {
        ["flow"] = "IMPORT", ["mainServiceId"] = DraftFixtures.Service(2).ToString(),
        ["source"] = new JsonObject { ["documentType"] = "EDO", ["referenceNumber"] = "EDO-REGISTRATION-LOCAL", ["blNumber"] = "BL-LOCAL", ["validUntil"] = Planned.AddDays(5).ToString("yyyy-MM-dd") },
        ["plannedAt"] = Planned.ToString("O"), ["contactName"] = "Import fixture", ["contactPhone"] = "0901234567",
        ["lines"] = new JsonArray(new JsonObject { ["sourceContainerId"] = DraftFixtures.Id(401).ToString(), ["services"] = new JsonArray() })
    };
    private static JsonObject Usage(int service, DateTimeOffset from, DateTimeOffset to)
        => new() { ["serviceId"] = DraftFixtures.Service(service).ToString(), ["startsAt"] = from.ToString("O"), ["endsAt"] = to.ToString("O") };
    private static async Task<JsonObject> Save(JsonObject input) => (await Status(200, "PUT", Guid.NewGuid().ToString(), input)).Json.AsObject();
    private static async Task<JsonObject> Validate(JsonNode draft) => (await Status(200, "POST", $"{G(draft, "id")}/validate", Version(draft))).Json.AsObject();
    private static JsonObject Version(JsonNode draft) => new() { ["expectedVersion"] = draft["version"]!.GetValue<int>() };
    private static JsonObject ConfirmInput(JsonNode draft, JsonNode quote) => new() { ["expectedVersion"] = draft["version"]!.GetValue<int>(), ["quoteToken"] = quote["quoteToken"]?.DeepClone() ?? JsonValue.Create(new string('F', 64)) };
    private static IEnumerable<JsonNode> Issues(JsonNode result) => result["issues"]!.AsArray().Select(row => row!);
    private static JsonNode Fee(JsonNode result, int service) => result["fees"]!.AsArray().Single(row => G(row!, "serviceId") == DraftFixtures.Service(service))!;
    private static Guid G(JsonNode row, string key) => Guid.Parse(S(row, key));
    private static string S(JsonNode row, string key) => row[key]?.ToString() ?? "";
    private static decimal D(JsonNode row, string key) => row[key]!.GetValue<decimal>();
    private static bool B(JsonNode row, string key) => row[key]!.GetValue<bool>();
    private static void Assert(bool valid, string message)
    { if (!valid) throw new Exception(message); checks++; Console.WriteLine("PASS: " + message); }

    private static async Task WithChanged(NpgsqlConnection pg, string table, string column, Guid id, object value, Func<Task> check)
    {
        // All identifiers are checker-owned literals, and startup only accepts the
        // dedicated local fixture database. Restore exact source values afterwards.
        await using var read = new NpgsqlCommand($"SELECT {column} FROM {table} WHERE id=@a", pg);
        read.Parameters.AddWithValue("a", id);
        var original = await read.ExecuteScalarAsync();
        await DraftFixtures.Sql(pg, $"UPDATE {table} SET {column}=@a WHERE id=@b", value, id);
        try { await check(); }
        finally { await DraftFixtures.Sql(pg, $"UPDATE {table} SET {column}=@a WHERE id=@b", original, id); }
    }
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
        var claims = new[] { new Claim("sub", DraftFixtures.Id(user).ToString()), new Claim("id", DraftFixtures.Id(user).ToString()), new Claim("role", role), new Claim("name", "Registration fixture") };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DraftFixtures.Secret)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(2), signingCredentials: credentials));
    }
    private sealed record Reply(int Status, JsonNode Json, string Raw);
}

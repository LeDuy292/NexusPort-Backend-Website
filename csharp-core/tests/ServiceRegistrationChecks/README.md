# Guarded service registration checks

This executable hosts the actual C# ServiceDrafts controller and registration
service with a dedicated PostgreSQL database. It never accepts configured/shared
application databases, and does not read appsettings.

Use only a new isolated cluster with these exact values:

- Host `127.0.0.1`, port `55445`, database `eport_registration_check`
- User `registration_check`
- PostgreSQL custom setting `nexusport.registration_test` containing a fresh marker

Set `REGISTRATION_TEST_CONNECTION` and `REGISTRATION_TEST_MARKER` for that cluster,
then run:

```powershell
dotnet run --project csharp-core/tests/ServiceRegistrationChecks
```

`--serve` keeps the fixture API listening on `127.0.0.1:5512` after the checks for
browser QA. All labels, users, containers and prices are local test fixtures. The
linked Part 2 fixture establishes private source identities; this checker adds
source lifecycle columns and applies migration 037 twice to check idempotence.

Checks cover missing information, company boundaries and current-account roles,
unverified eDO, source changes, explicit zero versus missing tariffs, price/date
priority, duration billing, stale previews, immutable confirmed prices, duplicate
requests and concurrent confirmations. A source fingerprint verifies that service
registration never changes containers, Booking, declaration, visit or access rows.

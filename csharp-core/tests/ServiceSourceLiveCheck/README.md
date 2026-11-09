# Development source verification

This opt-in loopback host verifies the real development samples with the actual
C# service controllers. It does not repair schemas or seed users/sources.

Run from the backend repository after migration 038 and the explicit development
sample script have completed:

```powershell
$env:SERVICE_SOURCE_LIVE_CHECK='development'
dotnet run --project csharp-core/tests/ServiceSourceLiveCheck
```

The protected C# development configuration is read in memory and must match the
project's development `.env` target. No connection values are printed. Startup
checks the two existing company memberships and their three DEMO Booking sources.

The host listens only at `127.0.0.1:5516`. Its fixture authentication key is only
accepted by this temporary test host, never by the application. Mutations are
limited to one deterministic `03820000-...` draft identity per demo company,
the exact note `DEMO e-Port verification`, and existing `DEMO-EPORT-...` sources.
It cannot modify source records or create users. Stop the process after QA.

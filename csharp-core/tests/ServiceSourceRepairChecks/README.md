# Guarded service Booking-source repair checks

This executable only accepts PostgreSQL `127.0.0.1:55446/eport_source_repair_check`
as `source_repair_check`. Before any fixture DDL, `SOURCE_REPAIR_TEST_MARKER` must
match server setting `nexusport.source_repair_test`; pass the connection through
`SOURCE_REPAIR_TEST_CONNECTION`. It refuses project/shared databases.

Run `dotnet run --project csharp-core/tests/ServiceSourceRepairChecks`.
`--serve` keeps the isolated real C# API at HTTP5514 for browser QA.

The fixture deliberately removes the four upstream Booking fields in the isolated
cluster to reproduce the reported503. It applies migration038 twice, checks
original legacy-row preservation and no inferred company assignment, creates
explicitly scoped new Booking/container records, and exercises the real HTTP
source picker, draft, fee validation and confirmation with tenant isolation.

# NexusPort yard database workflow

This system manages one storage yard inside a port. Transport companies declare
Container identity and cargo, then create separate Dropoff/Pickup Bookings for
vehicles, direction and appointment windows. Yard staff operate and track the
stored Containers; they do not create declarations.

- `containers`: unique physical identity plus the current cargo/power projection
  used by Booking and inventory.
- `container_visits`: one storage occurrence, including the carrier declaration.
  A new declaration leaves movement, appointment, source reference and B/L fields
  NULL. Seal is optional; blank values remain NULL. Existing operational/EIR history is retained.
- `booking_containers`: many-to-many history of Bookings and Containers. Dropoff
  and later Pickup can use different vehicles and drivers.
- `container_cargo_types`: stable cargo codes, Vietnamese names and power flag.
- `container_types`: equipment catalog; UI and generated Excel use these rows.
- `required_power_kw`: requested power in kW for frozen cargo. New frozen-cargo
  declarations require positive kW and refrigerated equipment. Existing records
  without a power declaration remain NULL; no invented default is assigned.

## Migrations

After the base schema, apply every file in `migrations/` in filename order:
`20260925_001`, `20261002_002`, `20261003_003`, `20261003_004`,
`20261009_005`, `20261012_006`, `20261017_007`, `20261017_008`.

Never change an applied migration. Each file records its version in
`nexusport_schema_versions`. Do not run `samples/` as part of deployment.

For an existing installation through 004, this narrowly scoped runner checks
only this task's 007/008. Develop's unrelated trip migrations are not applied by this runner. With DATABASE_URL supplied explicitly:

```powershell
node --env-file=node-core/.env database/scripts/apply-yard-declarations.cjs
# After reviewing the exact missing versions and target database:
node --env-file=node-core/.env database/scripts/apply-yard-declarations.cjs --apply
```

Migration 007 adds the cargo catalog and power columns/checks, and normalizes
reference equipment descriptions without changing IDs or existing weight specs.
It does not delete Containers, declarations, Bookings, users or EIR history.
Seal is optional declaration input and stored per visit with a current master
projection. B/L and reference columns remain for legacy operations, not declaration input.

Migration 008 documents optional seals on the existing nullable columns; it does
not recreate tables, change IDs, or erase historical seals.

Container declarations do not assign ownership or a transport company. The
transport company performing a trip belongs to Booking. The authenticated creator
is retained as container_visits.declared_by for audit and declaration access only;
it is not an ownership claim. New declarations leave transport_company_name NULL
and do not set containers.carrier_id. Existing historical fields are preserved.
The company field, account-company lookup and company-name matching rule have been
removed from declaration input. Download a fresh Excel template without that column.

## API

Only Carrier/Transport Company/Carrier Staff can POST manual or Excel declarations
under `/api/v1/containers/intake/transport`. GET `/template` generates a fresh
Excel workbook containing only one Container sheet, with all eight manual-entry
fields and no sample rows or catalog/instruction sheets. Imported equipment codes
and cargo names are still validated against the database. Stale import columns fail visibly.
Staff use registry/detail and operational status APIs. Node's port intake and
physical master create/update/delete routes have been removed.

## Validation

Node build and Jest cover declaration validation, Excel mapping, access rules and
status behavior. PostgreSQL integration tests are opt-in and hard-guarded to the
isolated database `postgresql://postgres@127.0.0.1:55438/nxp038_test`:

```powershell
$env:DATABASE_URL='postgresql://postgres@127.0.0.1:55438/nxp038_test'
$env:NXP038_INTEGRATION='1'
npm --prefix node-core test -- --runInBand
```

The integration fixture is created and removed only in that test database.

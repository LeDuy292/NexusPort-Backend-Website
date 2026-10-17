# NexusPort container and yard journey contract

This application manages one storage yard inside a port.

- `containers` is the shared physical identity (unique number, equipment type) and
  actual operational state. A declaration/booking never reserves or resets that
  state. Legacy cargo/company/seal columns remain for a rolling deployment and
  historical preservation; new workflows do not write or read them as cargo truth.
- `container_declarations` stores private cargo input per use: load status,
  customer, seal, cargo, weight and requested power. `carrier_id` is the access
  scope resolved from authenticated membership, not ownership of the container.
  Identical unbooked retries reuse a declaration. Changed input creates a separate
  declaration. Once booked it is immutable.
- `container_visits` represents one yard stay, created at Dropoff booking time.
  A declaration alone does not open a stay. Multiple planned stays are allowed.
- `booking_containers` links a physical identity, declaration and exact stay.
  Dropoff/Pickup can use different trucks, drivers and authorized companies.
  Pickup must follow inbound; a new stay must follow the previous planned exit.
  Without a planned exit, another stay cannot be reserved indefinitely on top.
- `container_visit_access` permits a company to book Pickup for a specific stay.
  A valid private pickup code can transfer this access when the Pickup booking is committed. Admin/dispatcher may also grant it explicitly. Other companies
  never receive the cargo of the original declaration through shared search.
- EIR and its seal/damage/media rows are immutable after issue. Drafts can be
  assembled before issue. A visit completion also freezes its historical data.
  Corrections require a separate audited correction workflow, not SQL overwrites.

## Read and write contract

Node `/containers/lookup?containerNumber=...` returns identity/type only.
`/containers/journeys` requires explicit search; pagination is bounded to 50 rows.
Carriers may filter only by number and receive identity fields only. Staff search
visits by number, normalized truck plate, customer or time range. Generic rich
container/detail/status endpoints are staff-only. No list loads on page entry.

`/containers/intake/transport/manual` and `/import` are carrier-only. The authenticated
user must have one unambiguous company membership. Manual and the single-sheet
empty Excel template share eight input fields. Seal is optional; frozen cargo
requires positive kW and refrigerated equipment. IDs returned include
`declarationId`, not a fabricated `visitId`.

C# Dropoff requires `containerSelections` with `containerId` and the exact `declarationId` passed from the declaration page. Pickup requires `containerId` and private `yardCode`; the server resolves the stay and copies the inbound cargo snapshot into the receiving company scope in the same transaction. Client-supplied visit/declaration IDs cannot replace the code for Pickup. Tenant is resolved from authenticated DB
membership; client carrier IDs cannot override it. C# returns the visit assigned
by the DB trigger. Changing dates/assignments revalidates the whole schedule.
Physical status is updated by actual gate/yard actions, not booking creation or
cancellation. Passed physical GateOut completes the Pickup stay and creates its
receipt; completing a Dropoff exchange retains an active stay.

The declaration screen passes the selected identity/declaration into Booking; the
Booking wizard has no declaration or stay dropdown. Its original two-step layout,
fleet recommendation, payload gauge, shift presets and e-Pass preview are retained.
`POST /Booking/recommend-selection` accepts the exact private declaration for Dropoff
or the pickup code for Pickup; the original matching algorithm uses this cargo and
only the authenticated company's fleet. It can also accept an authorized bookingId
for existing fleet assignment. Unknown weights never become a fictional 20 tons.

DB migration 012 generates a unique random `pickup_code` for each stay, formatted
`TC-XXXX-XXXX-XXXX` (48 random bits). This transferable capability is distinct
from the historical `visit_reference` and is never returned by Node's shared
lookup/search. Authorized Dropoff responses/details expose it for copying; save
success offers “Đặt Pickup ngay” with the code prefilled. Existing visit references
are preserved. Closed/canceled inbound stays and duplicate Pickups are rejected.
Failed booking validation rolls back access and copied declarations. Booking create
and recommendation requests have a per-authenticated-user rate limit (15/minute).
Times use local UI time and UTC API values; booking creation remains Pending.

## Migration and deployment

Driver pre-gate coordination uses additive migration
20261017_015_booking_dispatch_arrivals.sql and 016 for truck changes. Apply with
node database/scripts/apply-booking-dispatch.cjs --apply after its prerequisites.
It stores arrival/readiness and dispatcher calls separately from booking/gate
status, with durable notifications and a transactional socket outbox.
See docs/booking-dispatch.md for the flow and validation commands.

Driver demo migration `20261017_014_driver_booking_feed.sql` adds an explicit optional `drivers.user_id` link (unique per login) and persisted driver booking notifications. Run `node database/scripts/apply-driver-booking-feed.cjs --apply` after all earlier migrations. Existing profiles/accounts are never matched by name automatically. Admin must select company and a driver profile in that company while creating/editing a Driver account or assigning the Driver role. Express reads booking cargo only from the exact linked declaration. The booking trigger records assignment, approval, cancellation/rejection and schedule changes for the linked driver, including changes made by the C# website.

Read `AGENTS.md`. Never modify an applied migration; every correction is additive.
Apply all missing migrations in filename order, including develop's 005/006 where
those modules are deployed. Never run `samples/` automatically.

The exact allowlisted deployment runner checks develop's missing 005/006 and this feature's 007 through 013:

```powershell
node --env-file=node-core/.env database/scripts/apply-yard-declarations.cjs
node --env-file=node-core/.env database/scripts/apply-yard-declarations.cjs --apply
```

009 backfills declarations from exact visit records and links only explicit
`container_visits.booking_id` relations. It never guesses a relation from the most
recent container row. 010 adds transactional schedule/lifecycle/snapshot guards.
011 preserves unknown legacy cargo and validates the final schedule transaction.
Legacy columns, physical records, booking IDs and EIR history are retained.

The legacy reconciliation panel/modal and its automatic request are removed from
the frontend. Old bookings and their history are retained; no linkage is guessed.
The restricted backend reconciliation API remains available for explicit maintenance.

Deploy Node/C# and FE together with the migrations; restart old running services
before testing the new UI. Do not run the old C# booking writer against the new
link contract.

## Verification

Build Node/FE/C#. PostgreSQL integration uses only the exact isolated target:

```powershell
$env:DATABASE_URL='postgresql://postgres@127.0.0.1:55438/nxp038_test'
$env:NXP038_INTEGRATION='1'
npm --prefix node-core test -- --runInBand
```

`database/scripts/check-container-journey-http.cjs` checks real HTTP against
isolated Node :5189, C# :5188 and that same guarded DB. It tests declaration to
Dropoff to linked Pickup, public identity, tenant spoofing/reads, and invalid
rescheduling. Fixtures never run against the configured remote database.

The frontend dev proxy routes `/api/v1` to C# :5000, `/node-api` to Node Core
:4000 and other `/api` paths to Express :3001. Keep `VITE_API_URL=/api` for this
same-origin setup. A production reverse proxy must provide the same routing.
Driver container confirmation requires both bookingId and containerId; a completed
yard event must be after that booking's actual gate entry. Gate window comparisons
normalize Npgsql legacy DateTimes to UTC. Successful physical GateOut also releases
the current yard slot in the same transaction.

Migration 013 keeps the TC prefix for new/private pickup codes. Existing planned/active code suffixes are retained, and the API accepts the former YARD prefix as an alias. The payload analysis panel remains visible in loading/missing-data/error states as well as after a successful result.

Declaration search also returns `bookings` summaries (ID/code/direction/status).
These are linked by the exact declaration or one of its explicit stay IDs and
filtered to the authenticated company's membership. Reusing a physical container
never attaches bookings from another stay. The declaration detail buttons open
`/booking?bookingId=...`; the existing Booking detail API rechecks tenant access.

Each declaration booking summary includes `isDeclarationBooking`: true for its
own snapshot link, false for the opposite booking of the same stay. Use this to
label the row; never guess Dropoff from a missing `bookingType` property. Existing
non-canceled Pickups open their detail instead of offering duplicate creation.

BookingRepository updates rely on EF property-level tracking of the entity loaded
by the repository. Do not call DbSet.Update on the entire graph for approval,
assignment or cancellation: with Npgsql legacy timestamp behavior this rewrites
unchanged local DateTimes through timestamp parameters and can shift appointments
by seven hours. Approval must preserve appointment_start/end and valid_from/to.
The isolated HTTP regression places a valid Pickup seven hours after Dropoff and
asserts approval succeeds without changing any stored schedule instant.

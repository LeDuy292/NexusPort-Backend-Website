# Unified Manifest workflow (Node Core TypeScript)

The Shipping Line portal creates one Manifest containing B/Ls and their cargo
containers. The nested document is saved atomically in PostgreSQL; a failure to
reserve any B/L number rolls back the entire Manifest. Nothing is written to
physical `containers`, declarations, visits, bookings, or yard status.

Apply `database/migrations/20261113_043_unified_manifests.sql` before using the
new API. Inspect schema versions and apply missing prerequisites in filename
order. The configured database now has migration `20261113_043` applied and verified.
The migration runner reconciles the obsolete hard-coded `20261110_034` gate seed
when `20261025_022` already configured active GATE-01/GATE-02, preserving their IDs
and the current exit gate instead of reopening GATE-03 or duplicating gate codes.

```powershell
cd node-core
npm run db:check
npm run db:migrate
npm run db:audit-manifests
```

If PostgreSQL reports `remaining connection slots are reserved`, it has exhausted
ordinary client capacity; this is independent of schema versions. The current
shared instance was observed with `max_connections=20`, including 3 superuser
reserved slots. Do not terminate unidentified remote sessions. `npm run db:capacity`
prints grouped connection counts without modifying sessions or database settings.
Node Core defaults to a pool maximum of 2 and labels connections `nexusport-node-core`.
It warms and retains one shared idle connection, so pausing between form fields
does not force each lookup to compete for a new server slot. Excess idle clients
are returned after 3 seconds. If the last connection is lost, a bounded background
warmup reacquires it when capacity is available. Local `.env` also uses
`DB_POOL_MAX=2`.

Startup schema verification retries connection acquisition for up to 30 seconds
when PostgreSQL specifically returns SQLSTATE 53300. It still refuses to start
against missing migrations or a permanently unavailable DB. API connection
acquisition has a short bounded retry; exhausted capacity returns HTTP 503 with
`Retry-After: 3`. Queries and transactions are never automatically replayed. The
checker closes its pool even when acquiring its first connection fails.

Development startup checks for pending migrations and starts the workspace-local
RabbitMQ broker. On a new Windows machine, run `npm run dev:rabbitmq -- -Install`
once to download official Erlang 27.3.4.19 and RabbitMQ 4.3.4 ZIPs. No system service
or Docker installation is required. Runtime data is stored under
`outputs/local-rabbitmq` outside the source repository. Remote RABBITMQ_URL settings
are respected and do not start a local broker. Production still uses its configured
broker. Broker disconnects reconnect consumers with bounded backoff; HTTP APIs
continue to work while the broker is unavailable.

The user-requested Manifest/B/L reset was verified on the configured database:
both new tables and all legacy Manifest/B/L tables were empty. The reset tool
only deletes the two new aggregate tables, after saving a snapshot under
`outputs/manifest-reset`; it does not delete eDO, operational bookings, accounts or
physical containers. All 110 physical containers were retained. Express sample
Manifest/B/L/SI/eDO/reconciliation fixtures were removed, and the browser clears
only old Manifest/generated-booking cache keys once on the next app load, so sample
declarations cannot reappear after a restart. Existing independent yard reservations
and port operational history are retained.

All endpoints require a valid HS256 Bearer token and an active `Shipping Line`
user. Documents and B/L number uniqueness are scoped to the authenticated account.
Caller-provided owner IDs are ignored. The frontend retains the Express history
adapter, but its sample Manifest/B/L datasets were cleared for the requested reset.
Old routes remain available without automatically recreating demo declarations.

Container numbers must already exist in the physical `containers` registry. The
authenticated `GET /api/v1/shipping-line/manifests/container-lookup?containerNumber=...`
returns only identity ID, number, catalog type, registered ISO size/type and reefer
flag (or null if absent). The composer shows missing numbers inline and can open
container registration with the number prefilled. It stores the unfinished Manifest
and currently edited B/L in account-scoped sessionStorage; successful registration
returns to that B/L and rechecks the number. No unfinished document is submitted to
the server merely to navigate to registration. Closing or saving clears the session.

At save time the repository locks and reads every registered identity in the same
transaction. Missing numbers reject the entire write, including draft saves. ISO,
identity IDs and reefer fields supplied by the caller are overwritten from the DB;
they cannot create equipment or change its technical attributes through Manifest.
No additional SQL migration is required for these existing registry fields.

## Container ISO data cleanup

Migration `20261113_044_container_iso_quality.sql` adds a DB checksum function,
an explicit identity quarantine flag and an audit table. The user-requested repair
corrected only the final check digit on 6 identities, preserving their IDs, owner
codes, serial numbers, equipment and operational links. Five ambiguous identities
(invalid category letters or collisions with an already registered number) were
quarantined instead of deleting referenced declarations/bookings. Historical
snapshots were not rewritten. The DB retains 110 physical rows: 105 selectable
valid identities and 5 quarantined identities excluded from identity search and
Manifest lookup/save. The validated CHECK constraint prevents new invalid active
identities. Repairs are recorded in `container_identity_repairs` and a before
snapshot under `outputs/container-iso-repair`.

Run `npx tsx src/scripts/repair-container-iso.ts` to inspect a plan; only `--apply`
changes data. It refuses concurrent identifier changes and checks the complete
active inventory before committing. Corrected test examples are `MSKU8829101`
(20GP) and `CMAU3381923` (20RF). These corrections establish checksum validity in
the development dataset, not external verification of physical box markings.

| Method | Node Core path | Behavior |
| --- | --- | --- |
| GET | `/api/v1/shipping-line/manifests?direction=IMPORT` | List own documents |
| POST | `/api/v1/shipping-line/manifests` | Save complete aggregate once |
| GET | `/api/v1/shipping-line/manifests/:id` | Read own document and revision |
| PUT | `/api/v1/shipping-line/manifests/:id` | Edit own draft with current revision |
| POST | `/api/v1/shipping-line/manifests/:id/send-to-port` | Mark completed document as sent |
| POST | `/api/v1/shipping-line/manifests/:id/recall` | Recall sent document |

Create/update body:

```json
{
  "requestKey": "837853fb-b149-4397-924b-24f7e552ca8d",
  "voyageNumber": "EG-2605W",
  "vesselName": "EVER GLORY",
  "direction": "IMPORT",
  "isDraft": false,
  "bills": [{
    "blNumber": "BL-001",
    "shipper": "Exporter",
    "consignee": "Importer",
    "containers": [{
      "containerNumber": "CSQU3054383",
      "isoType": "40RF",
      "sealNumber": "SL-001",
      "grossWeightKg": 23000,
      "commodity": "Frozen fish, cartons",
      "isReefer": true
    }]
  }]
}
```

Update includes `revision` from the detail response. Only drafts can be changed.
Same-key/same-content POST retries return the saved document; changed content
under the same key returns 409. A container number cannot occur twice within one
Manifest, even under different B/Ls. ISO checksum, seal, positive VGM up to
36,000 kg and a nonempty cargo description are validated server-side. Reefer
equipment codes imply `isReefer`; no temperature/humidity/ventilation is required
or invented. No container operational status is accepted from the form.

The existing browser-local `manifestPortSyncService` still performs the portal's
Gate Booking synchronization on send/recall; this change does not introduce a
server-to-server port acceptance or weighbridge reconciliation workflow. New
B/Ls are nested snapshots, not legacy Express in-memory B/L records. Integration
with the legacy eDO/fee/surrender workflow is outside this change; those endpoints
continue to operate on their existing records.

Frontend uses `/node-api/shipping-line/manifests`, so run Node Core on 4000 and
the existing voyage/history Express service on 3001. Old `/shipping-line/bills-of-lading`
links redirect to Manifest. Existing history is retained; new cargo is never
silently written back into sample records.

Validation: FE build and lint (existing warnings), Node TypeScript build, plus
schema/transaction/API tests against a separate local database:

```powershell
$env:DATABASE_URL='postgresql://postgres@127.0.0.1:55443/manifest_test'
npm --prefix node-core test -- --runInBand src/modules/manifest/manifest.test.ts
```

The integration suite only runs for that exact isolated database URL.

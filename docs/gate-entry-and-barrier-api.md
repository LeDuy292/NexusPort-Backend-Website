# Dispatcher → gate entry → barrier API

Operational booking is not duplicated. Express dispatch commands persist
`booking_dispatch_state`; migration 018 creates a linked, one-use
`gate_entry_orders` row in the same transaction through a trigger:

1. Call: `pending_ack`, existing 10-minute acknowledgment deadline.
2. Driver acknowledgment: `active`, entry deadline 15 minutes after acknowledgment.
3. AI recognition → C# verification: must match active permission, booking,
   assigned truck/driver and normalized plate. Booking appointment is not reused
   as the short entry deadline. Gate staff see the queue, including pending calls.
4. Successful verification → barrier command API; simulation response only.
5. Passage event → passage API, consumes the permission and writes canonical
   `gate_transactions` and booking `checked_in` atomically. Smart Route then sees
   Gate In. Retries reuse the existing gate transaction.
6. No acknowledgment, expired entry permission, withdrawal, delay or changed
   booking assignment invalidates entry. The worker returns expired calls/entry
   permissions to ready; the business booking is not canceled.

No existing calls are silently backfilled as new entry permissions. After
deployment, withdraw/reissue an old call to create a permission.

There is one entrance (`GATE-01`) and one exit (`GATE-03`). Migration 020 retires
GATE-02 without deleting its historical transactions and publishes a new map
revision, retaining road restrictions that still apply.

## API contract (C# :5000, API prefix /api/v1)

All command/entry endpoints require the authenticated Gate Officer or admin.
The request actor is obtained from the JWT, never from a client officer ID.

- `GET /Gate/entry-orders`: `{items,serverTime}`, latest 200 orders in 24 hours.
  Frontend polls every 15 seconds and displays failures rather than claiming an
  empty or current queue. Driver/dispatcher booking responses include
  `gateEntryOrder` with its effective status and deadline.
- `GET /Gate/bookings`: read-only paged calendar for gate staff. It reuses the
  Booking query service, with page size bounded to 150. Generic `/Booking`
  authorization is unchanged; Gate Officer still cannot create/approve bookings.
  Gate calendar days use Asia/Ho_Chi_Minh instead of UTC, and load failures are
  visible. Calendar and entry-order polling use a 15-second fallback.
- `POST /Gate/verify`: existing AI/manual rule pipeline. Manual input includes
  `verificationType: "MANUAL_GATE_IN"` and `manualReason`; it still requires an
  active dispatcher permission. AI service remains responsible for recognition.
- `POST /Gate/barrier-commands`:

```json
{
  "requestId": "a5f1e353-e873-43d6-a01a-e36a6dd00174",
  "gateCode": "GATE-01",
  "action": "open",
  "source": "automatic",
  "verificationRecordId": "d0d55338-21d6-46dc-8c0d-2093b97e917c"
}
```

`action` is `open`/`close`, `source` is `automatic`/`manual`. Manual commands
require `reason`. Opening needs a PASS verification from the last two minutes
for the same active gate and direction; entry also checks its permission again.
Exit requires completed work, prior entry, container confirmations and no prior
exit. Close does not require a vehicle verification. Request ID is the idempotency
key; reuse it for retries of the same command. Response:

```json
{
  "commandId": "a5f1e353-e873-43d6-a01a-e36a6dd00174",
  "gateCode": "GATE-01",
  "action": "open",
  "status": "simulated",
  "source": "automatic",
  "physicalState": "unknown"
}
```

No endpoint in this release controls a real barrier. Persisted commands are the
boundary for a future authenticated IoT adapter: command ID, gate, action,
requested/acknowledged/failed states and device acknowledgment. An acknowledgment
of opening must remain separate from a vehicle passage event.

- `GET /Gate/barrier-commands?gateCode=GATE-01`: last 50 command records.
- `POST /Gate/passage-in` (legacy alias `/Gate/approve-in`):
  `verificationRecordId`, `bookingNumber`, `vehiclePlate`, `gateCode`, optional
  `containerNumber`, and `passageConfirmed:true`. Requires an accepted simulated
  or acknowledged open command for that verification/permission. The temporary
  frontend simulator supplies the passage event after the open interval below;
  a future authorized crossing sensor can use this API. Merely scanning,
  recognizing or opening does not create Gate In.
- Gate Out continues through Node `/gate/complete-out` after the passage event.

## Temporary timed passage simulator (NXP-114)

Gate Control runs the existing server verification and barrier-open checks. Only
an accepted `simulated` open command starts a countdown. The default interval is
10 seconds; frontend `VITE_GATE_OPEN_SECONDS` accepts whole seconds from 3 to 120
(invalid or missing values use 10). Change the environment value and restart Vite
or rebuild the frontend to change the interval.

When the countdown expires, Gate Control automatically calls C#
`POST /api/v1/Gate/passage-in` with the verified evidence and
`passageConfirmed:true`, or Node `POST /api/v1/gate/complete-out` with the booking
and exit gate IDs. Existing server business rules and transaction idempotency
remain in force. Successful Gate In makes Smart Route available; Gate Out
advances the journey to exited. Driver views observe these through their existing
refresh/realtime mechanisms. The simulator then issues an automatic close
command, including when recording passage fails.

The UI shows the countdown and simulation status and no longer requires a staff
passage-confirmation click. While a cycle is pending, another scan, gate/direction
change or manual barrier command cannot interrupt it. API failure stays visible;
retry reuses the same evidence and close request ID and skips passage if it has
already succeeded.

The timer runs in the browser, continues during navigation within the app, and
stores the pending cycle (without images) in account-scoped session storage.
Reloading and reopening Gate Control in the same tab resumes its original
deadline. Closing the tab/browser stops this temporary simulator; it is not a
server worker or a measurement of actual vehicle movement. For IoT, replace the
timed passage adapter with the sensor event and keep barrier acknowledgment
separate from passage.

Manual fallback covers failed recognition/weather by entering the plate and
reason and running the same server checks. It does not bypass an expired or
missing permission. If the backend itself is down, this UI cannot command a
barrier; a physical emergency-control procedure is outside this software API.

## Deployment and checks

Migrations 018/019/020 are additive; never run sample fixtures against the shared database.
Inspect all missing migrations first:

```powershell
node --env-file=node-core/.env database/scripts/apply-gate-entry-orders.cjs
# After explicit approval for mutations against the configured DB:
node --env-file=node-core/.env database/scripts/apply-gate-entry-orders.cjs --apply
```

The runner includes every missing prerequisite in filename order. Following
explicit user approval, reefer migration 005 and gate migrations 018/019/020
were applied and verified on the configured database. The reefer file records
legacy version `005`; the runner recognizes this alias to avoid repeated DDL.
Migration 019 supplies the missing C# evidence table and gate-code mapping.

Build/checks that do not modify the configured DB:

```powershell
dotnet build csharp-core/src/NexusPort.Api/NexusPort.Api.csproj --no-restore
dotnet run --project csharp-core/tests/GateFlowChecks/GateFlowChecks.csproj
npx jest tests/booking-dispatch.test.js tests/booking-dispatch-worker.test.js tests/booking-dispatch-socket.test.js --runInBand
# Frontend: npm run build; App: npx tsc --noEmit
```

`tests/integration/gate-entry-orders.cjs` is a SQL lifecycle check against a
dedicated throwaway PostgreSQL instance at 127.0.0.1:54818, user `gate_flow_test`.
It deliberately ignores `.env`/`DATABASE_URL`, verifies the server address/port
and account, and recreates only its `gate_entry_fixture` schema. It exercises
the real 015/016/018 triggers, entry activation/expiry/revocation/consumption,
simulation versus passage, and migration retry. It is not a shared-DB deployment
or a hardware/end-to-end camera test.

`tests/integration/check-gate-entry-http.cjs` checks the running C# API against
the configured DB using existing accounts for read-only requests. Invalid
commands are rejected before persistence. It verifies 200/401/403/422 responses,
the canonical gate pair and active map revision; it does not simulate a real
truck passage in the shared database.
# Gate timestamp comparisons

`Npgsql.EnableLegacyTimestampBehavior` remains enabled by the existing API.
Consequently, PostgreSQL `timestamptz` values are materialized as local
`DateTime` values. Before comparing entry activation, expiry or verification
freshness with `UtcNow`, the gate code converts both sides to UTC. Comparing
the original clock ticks can falsely reject a live permission in Vietnam and
allow a stale verification to appear fresh.

The entry queue and verification response use the same expiry instant. Failed
scans do not consume an entry permission or a dispatch call. There is no quota
on the number of calls: an acknowledged, unexpired order is reused; after expiry
the dispatch worker returns the booking to ready for a new call and driver
acknowledgment.

Validation for the timezone repair: 38 gate flow checks, including local/UTC
activation and expiry boundaries and stale/future verification rejection. The
running API returned PASS for plate `29Y3-036.58` and the existing active booking
after restart, without changing the permission, opening the barrier or recording
physical passage during the API check.

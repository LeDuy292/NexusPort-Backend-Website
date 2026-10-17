# NXP-114 integration with develop, 2026-10-18

Fetched and integrated these upstream develop tips into the existing
`feature/NXP-114-smart-route` branches:

| Repository | Upstream develop | Local integration |
| --- | --- | --- |
| Backend | `4ba8b4c` | merge `42ff3f8` |
| Website | `e47a8cb` | merge `caf681d` (includes initial merge `2842cc6`) |
| App | `5a61154` | fast-forward |

All three develop tips are ancestors of their respective feature heads. Work in
progress was first captured in Git stashes, including untracked files; those
snapshots were retained after applying the changes.

## Resolution and preserved behavior

- Website GateBookings uses the complete new develop implementation: day/week
  views, date filters, realtime refresh, silent synchronization and detail view.
  Only entry-order panel and canonical gate labels/options were added there.
- Website login, layout session cleanup, API client, Express client and
  DriverSmartRoute changes remain identical to develop. The session-kicked
  listener retains upstream socket/disconnect handling; its HTTP fallback was
  subsequently reduced from 2.5 to 15 seconds at the user's request, with hidden
  tab suppression, in-flight deduplication and stale-response protection.
  The final fetch discovered and included the additional session-kick heartbeat,
  disconnect listener, driver realtime and Node API client updates in `e47a8cb`.
- Backend CurrentUser claim fallbacks, Driver/Vehicle controllers and Node Core
  connection configuration remain identical to develop.
- User assignment validation combines develop's optional empty-field handling
  with PostgreSQL GUID normalization used by the configured carrier directory.
  A regression case covers creating non-Driver accounts with empty assignments;
  existing cases retain required Driver assignments and real directory GUIDs.
- NXP-137 transport-trip status/history flow is retained unchanged. Driver trip
  progress is distinct from canonical physical gate_transactions; a driver's
  progress button does not replace gate verification and passage confirmation.

Core NXP-114 behavior remains: original booking → dispatcher call → timed,
one-use gate entry permission → AI/manual verification → audited barrier command
API → physical passage → Gate In → yard Smart Route → container exchange → Gate
Out. One entrance GATE-01 and one exit GATE-03 are authoritative.

## Validation

- Website production build, Node Core TypeScript build and App typecheck passed.
- Dispatch, worker/socket and merged assignment validation: 39 assertions passed.
- Develop's unmodified NXP-137 lifecycle suite: all 11 tests passed on a separate
  local PostgreSQL database `nxp_driver_lifecycle_test` at 127.0.0.1:54818. Its
  initial default-DB attempt timed out in setup; the test requires a writable
  isolated DB because it runs syncDB(alter:true) and inserts fixtures.
- C# API built; 24 database-free gate checks passed, including develop's id-claim
  fallback and denial of retired/wrong-direction gates.
- Local SQL fixture checks cover actual gate entry triggers and lifecycle.
- Live C# read/denial checks returned 200 for queues/history, 401 anonymous,
  403 Driver, and 422 for invalid open commands, with no test command persisted.

The barrier is still a server-side simulator (`physicalState: unknown`). Camera
hardware and IoT passage sensing were not tested. Backend/frontend feature
changes remain reviewable separately from the upstream merge commits.

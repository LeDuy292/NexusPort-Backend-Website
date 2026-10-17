# Driver arrival and dispatcher coordination

This flow ends at the driver's acknowledgment of a dispatch call. It does not
change booking status, create GateIn, decrement yard occupancy or bypass gate checks.

- App: booking detail → confirm arrival and readiness in the authorized holding
  area → wait for dispatcher → acknowledge call. A driver can temporarily mark
  themselves not ready.
- Website: **Lệnh Điều Phối Xe** (/dispatch), reusing the previous demo page.
  Select an approved booking to call the ready driver, send a delay with reason,
  or withdraw a call. No sample tasks or fabricated yard occupancy are displayed.
- The queue reserves its detail column even when no booking is selected, so
  opening or closing details does not resize the list. The calendar is deferred.
- Express: GET /api/booking-dispatch/board?date=YYYY-MM-DD for Dispatcher/Admin;
  POST /api/booking-dispatch/:bookingId/:action for authorized role actions.
  Every command includes the last expectedVersion; stale commands return 409.

Defaults in dispatch.policy.js: arrival begins 30 minutes before appointment,
late arrival is more than 10 minutes after its start, call response deadline is
10 minutes. Readiness and arrival do not authorize early calls. A port delay
does not make a punctual driver late.

Migration 015 adds dispatch state, notification and outbox tables/triggers;
migration 016 invalidates stale coordination when a truck/plate changes.
Apply with node database/scripts/apply-booking-dispatch.cjs --apply after all
prerequisites. The DB trigger also invalidates readiness/calls when C# changes
the assigned driver, appointment or booking status.

server.js starts the outbox/expiry worker. State, driver notification and outbox
commit together. Socket.IO emits booking:dispatch_updated after that commit to
dispatcher:all and driver:<LOGIN USER ID>; a fleet driver ID is never a room ID.
Events only invalidate the HTTP snapshot and contain IDs/version, not cargo.
Clients reload on connect/reconnect, resume, events and a 15-second fallback.
An event arriving during a snapshot request queues another request. Duplicates
are harmless; the DB remains authoritative.

Vite proxies /socket.io to Express :3001. An absolute API URL uses its origin
for sockets; set VITE_DISPATCH_SOCKET_URL if using a separate reverse proxy.
Restart Vite after changing its proxy and restart Express for the worker.
Phone API configuration must use the computer's reachable LAN origin.
No background mobile push notifications are implemented.

Validation:

- npx jest tests/booking-dispatch.test.js tests/booking-dispatch-socket.test.js --runInBand
- App: npx tsc --noEmit, npm run test:bookings, npm run test:auth
- Website: npm run build
- Opt-in real SQL/HTTP/socket fixture:
  DISPATCH_TEST_DB=postgresql://postgres@127.0.0.1:54817/nxp_driver_dispatch_test
  then node tests/integration/booking-dispatch.cjs.
  The script refuses any other target and resets only that dedicated test DB.

Socket room broadcasts currently assume one Express process. Multiple processes
require a shared Socket.IO adapter and coordinated outbox delivery.

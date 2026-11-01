# Plate text and gate booking selection

The truck master contains `29Y3-036.58`, while existing booking snapshots can
contain `29-Y3036.58`. Both represent the identity `29Y303658`. Gate entry orders
store this compact identity; C# vehicle validation and entry permission lookup
remove separators before comparing. A different digit remains a mismatch.

The frontend displays this identity as `29Y3-036.58`. Gate calendar searches
ignore separator positions. The former fixed three-character display prefix
could not handle series such as `Y3` or `LD`.

When gate staff search by plate, the verification screen must not send the first
historical matching booking as an explicit booking number: that booking may be
canceled, or another trip may have an active entry permission. C# resolves the
plate using the current entry order. Only a booking code explicitly entered by
staff may select that exact booking; server validation still applies.

The observed failures included `BOOKING_INVALID_STATUS` for a canceled booking
and `BOOKING_NOT_FOUND` after the previous entry permission expired. Neither
failure was a separator mismatch. An expired permission still requires a new
dispatcher call and driver acknowledgment. Formatting never grants entry.

No booking snapshots or verification history are rewritten by this change.

Validation:

- Frontend: `node --test tests/plate-number.test.mjs`.
- C#: `dotnet run --project tests/GateFlowChecks --no-restore` from `csharp-core`.
- A read-only query against the configured DB confirmed both plate spellings
  resolve to the same currently active entry order and booking.

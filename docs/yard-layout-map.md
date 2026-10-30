# Container yard map

The website routes `/yard`, `/yard/map` and `/yard-staff/map` open the same yard
map. `/yard-ops` retains the movement workflow. Dispatch (`/dispatch`) and the
React Native driver journey use the shared drawing through their route snapshots.
Yard layout, directory, creation and maintenance are JavaScript Express endpoints.
Booking orchestration and its slot-reservation transaction remain in C#.

`node-core/src/shared/yard/reference-layout.json` records the reference drawing:
66 areas (A1–A21, B1–B17, C1–C12, S1–S5, D1–D11), 376 numbered ground
positions, Vietnamese labels and notes, GATE-01 entry and GATE-02 exit. Original irregular row
numbers, reversed C/S order and the four red source highlights are preserved.
Red source highlights do not set maintenance, restriction or occupancy.
The frontend has an identical bundled copy so the drawing remains available when
the inventory service is offline. Keep both copies identical when editing it.

## Inventory and route coordinates

`GET /api/yard/layout` returns the drawing, current slots, physical container
positions and the count outside the drawing. It is staff-only, authenticated,
uncached and read-only. Ground positions match the exact `(block, bay, row)`;
tiers come from the database. Missing positions remain unconfigured. Existing
extra rows and block R remain accessible in the inventory panel.

`GET /api/yard/blocks` supplies the same directory to Booking's yard selector.
`POST /api/yard/blocks` creates a block and its slots in a single transaction.
`POST /api/yard/slots/:id/maintenance` sets an explicit maintenance state and
rejects reserved/occupied slots and current physical placements. Both writes
require Yard Operator or Administrator.

The drawing is proposed until the active road graph declares the matching
`layout.drawingVersion`. Website and mobile clients overlay an assigned route
only when the drawing version matches. The old route-map view and view-switching
controls have been removed from both clients; only the new yard drawing is shown.
Coordinates from different versions are never mixed.

## DB configuration update

Migration `20261025_022_exit_gate_02.sql` renames the existing exit GATE-03 to
GATE-02 while preserving its UUID. The retired, inactive former entrance GATE-02
is retained as GATE-02-LEGACY, preserving its UUID and historical transactions.
The migration clones the old active map with the new gate code and retains its
opaque node IDs and all conditions. It aborts if the old GATE-02 is active or the
legacy code is already occupied. No slot/container inventory is added or moved.

The prepared publisher applies this migration when missing, then creates the
`smart_yard_maps` revision,
`NXP-yard-excel-v3`, and deactivates the previous revision atomically. It preserves
all slots, capacity, container positions, bookings, gate transactions and old map
revisions. Approaches stay in aisles beside stacks. Existing A-S1/A-S2 handover
locations and known slot node identifiers remain valid. Block R has no location
in the reference and remains explicitly unmapped for navigation.

Build Node Core, then review a read-only candidate:

```powershell
node node-core/node_modules/typescript/bin/tsc -p node-core
node --env-file=node-core/.env database/scripts/prepare-excel-yard-map.cjs --out=yard-map-candidate.json
```

Publish only after the user has approved the configured DB update, using the
exact current version returned by the dry run:

```powershell
node --env-file=node-core/.env database/scripts/prepare-excel-yard-map.cjs --apply --expected-version=NXP114-single-entry-exit-v2
```

The gate migration and new drawing are published in the same transaction.
Until approval, backend gate and booking checks support the active old exit code
as well as the new code, without changing data. New clients display GATE-02.

The publisher checks for concurrent map changes, unmapped recorded driver
positions, and road restrictions that cannot be carried forward. It rolls back
instead of discarding them. A second run against an already-active version is a
no-op. The previous revision is retained for a reviewed rollback. Gate migration
20261025_022 is recorded in `nexusport_schema_versions`; the map revision remains
separately versioned in `smart_yard_maps`.

Applied to the configured shared cloud database on 2026-10-25 with user approval:
gate migration `20261025_022` and active map `NXP-yard-excel-v3`. Verification
confirmed the exit gate UUID and all 252 slot UUIDs were preserved. Counts
remained 5 current container positions, 17 bookings and 4 gate transactions.
Other environments use the review/apply commands above; do not reapply on this DB.

The reviewed dry run on 2026-10-25 found 252 existing slots, no unmapped recorded
driver positions and no active restrictions. All 252 slots are preserved. Twelve
slots in block R have no road mapping. The inventory panel additionally lists
40 positions from A's sixth row outside the source's five-row drawing.
Coordinates, distances and travel times remain schematic and are not surveyed.

## Validation

Tests cover original numbering, missing versus empty inventory, maintenance
conflicts, atomic block creation, directed routes to handovers and exit, and
segments staying outside every storage rectangle. Website browser checks exercise
the actual `/yard` and `/dispatch` routes, narrow-screen containment, source
selection, stale inventory and unavailable-inventory behavior. DB validation is
read-only except for the explicitly approved map/gate publication above.

# Yard map: shared Tiên Sa design and existing inventory

The web routes `/yard`, `/yard/map`, and `/yard-staff/map` use the Tiên Sa design.
Dispatcher monitoring and the driver web/app views use the same geometry.
The DEV-only `/preview/yard` page does not create inventory or operational records.

## Source and generated files

The source is in the sibling frontend checkout:

- `src/features/yard-design/tienSaLayout.json`: investment-plan reference and block planning.
- `src/features/yard-design/geometry.js`: shared presentation geometry and access policies.
- `scripts/export-tien-sa-plan.mjs`: generates the backend graph, native SVG, and web version manifest.

Run the exporter from the frontend checkout after changing the design. Ship all
three repositories with matching versions. The backend consumes
`node-core/src/shared/yard/tien-sa-plan.json` through `tien-sa-map.ts`. Clients only
draw an assigned route when its `terminalPlan.version` matches their local design.

The design has 32 blocks in seven sectors; D01/D02 are the proposed reefer area.
This is an application design choice, not a confirmed official Tiên Sa reefer location.
Bay module counts for A–D follow the
user's counts; E–G and six rows/four tiers remain design assumptions. It creates
no database blocks, slots, reservations, or container placements. Coordinates are
schematic drawing units. Relative lengths select a basic corridor; distance and
ETA are null rather than presented as measured metres or seconds.

B/C use through aisles and continue to the rear cross road. A/E/F return to the
same access road; E/F have no mountain-side rear connector. Turning follows yard
staff instructions, with no certified turning radius implied by the drawing.
The outer A01/A04 aisles and B04/E01 connection belong to the shared geometry.

Existing booking, yard completion, driver confirmation, and physical gate passage
remain the journey state machine. New-plan destinations use the shared graph;
legacy destinations retain the configured map provider. Expired maps, invalid
coordinates, maintenance, and restrictions that cannot be translated between map
versions do not receive fabricated directions.

## Database publication — 2026-11-04

The user requested migration of both the existing inventory and open bookings,
then selected D for reefers and C for ordinary containers. Publication applied
additive schema migration `20261104_030` and the matching Tiên Sa map revision:

- 32 active blocks and 8,786 configured, non-overlapping physical storage envelopes.
- Five current placements: three ordinary containers in C and two reefers in D.
- Twelve container links from eleven open bookings receive destinations in C/D. A shared exact visit uses one envelope; a future visit of the same container gets its own envelope.
- Twelve existing plugs now point to D. All five running power sessions retain their identities, status, plug linkage and history. No power session is started/stopped by this migration.
- Pending inbound cargo is reserved, not marked as physically in the yard. Five current placements remain five; gate events and booking approval/completion timestamps are untouched.
- The legacy A/R blocks and 252 slot IDs remain inactive for EIR/history references. Four previous map revisions are retained. Unlinked unfinished sample tasks are soft-archived.
- Reefer migration `20261012_005_reefer_power_management.sql` was already applied under legacy version `005`; it was not missing.

Ordinary empty envelopes generally hold 40ft, with 20ft envelopes at remaining
module positions. Assigned 20ft cargo has explicit one-module envelopes; the one
45ft container has four modules reserved around its even-bay anchor as conservative
end clearance. This reserves space, not a claim that a 45ft container is 80ft long.
The database trigger rejects overlapping/out-of-bounds configured footprints.
Changing a 40ft envelope to two 20ft envelopes is a future explicit layout operation.

`database/scripts/prepare-tien-sa-map.cjs` performs a read-only dry run by default.
`--apply --expected=<fingerprint>` checks an exact reviewed snapshot, acquires locks
without waiting on a partial lock set, writes a local backup under sibling
`db-backups/`, and commits schema/data/map changes together. Monitoring heartbeat
timestamps are excluded from the fingerprint; session identity/state/plug/readings
are still checked and never overwritten. Re-running an already-published revision
is a no-op. This is a one-time bootstrap, not a background allocator for future
bookings. Do not delete historical revisions or applied migrations.

Verify using `database/verify/20261104_verify_tien_sa_layout.sql`. The pre-publication
backup is `db-backups/tien-sa-2026-11-04T14-30-24-998Z.json` in the workspace parent.

## Legacy compatibility still in use

Express `GET /api/yard/layout` and `GET /api/yard/blocks` still read existing slots
and physical placements for inventory/booking, filtering the active block directory.
The layout response adds the current `terminalPlan.version`, module footprints and
reserved-container labels. The web's protected map reads this live snapshot;
the unauthenticated DEV preview stays design-only. The legacy
`node-core/src/shared/yard/reference-layout.json` response remains compatible for
historical consumers; it is not used to draw the current Tiên Sa UI.

`ConfiguredYardMapProvider` reads configured maps and live restrictions for legacy
destinations. `build-yard-map.ts`, its tests, and
`database/scripts/prepare-excel-yard-map.cjs` support the historical Excel-map
deployment/rollback workflow. They are not the Tiên Sa exporter and must not be
used to publish the new design. Migration `20261025_022_exit_gate_02.sql` preserves
gate UUID/history while renaming the exit to GATE-02; legacy journeys retain their
recorded gate codes.

## Verification

Frontend geometry/position tests check block clearance, access roads, and bay/row/
tier limits. Node tests cover shared routes, legacy-provider compatibility,
restrictions, assignments, arrival, confirmation, and exit. App tests cover its
journey snapshot and API contract; TypeScript and web/backend builds must pass.
Fixture-based HTTP tests must use the documented isolated database, never the
configured shared DB.

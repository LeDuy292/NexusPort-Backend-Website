"use strict";
// Dry-run by default. Apply only an exact reviewed fingerprint, with a local backup.
const fs = require("node:fs"),
  path = require("node:path"),
  { createHash } = require("node:crypto");
const { Client } = require("../../node-core/node_modules/pg");
const design = require("../../node-core/src/shared/yard/tien-sa-plan.json");
const {
  buildRelayoutPlan,
  stableId,
  openBooking,
} = require("./tien-sa-relayout-plan.cjs");
const tables = [
  "yard_blocks",
  "yard_slots",
  "container_positions",
  "yard_slot_reservations",
  "yard_tasks",
  "driver_route_instructions",
  "driver_route_progress",
  "reefer_plugs",
  "reefer_power_sessions",
  "bookings",
  "booking_containers",
  "container_visits",
  "containers",
  "container_types",
  "smart_yard_maps",
  "smart_yard_conditions",
];
async function readState(db) {
  const state = {};
  for (const t of tables)
    state[t] = (await db.query(`SELECT * FROM ${t} ORDER BY 1,2`)).rows.sort(
      (a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)),
    );
  for (const l of state.booking_containers)
    l.direction = (
      await db.query("SELECT yard_booking_direction($1,$2) AS direction", [
        l.booking_id,
        l.container_id,
      ])
    ).rows[0].direction;
  return state;
}
// The monitoring service updates these heartbeat timestamps continuously. We
// never write power-session rows; retain them in the backup but compare every
// other session field, including identity, plug, status and readings.
const fingerprint = (state) =>
  createHash("sha256")
    .update(
      JSON.stringify({
        version: design.version,
        state: {
          ...state,
          reefer_power_sessions: state.reefer_power_sessions.map(
            ({ last_checked_at, updated_at, ...session }) => session,
          ),
        },
      }),
    )
    .digest("hex");
async function main() {
  const apply = process.argv.includes("--apply"),
    expected = process.argv.find((a) => a.startsWith("--expected="))?.slice(11);
  if (apply && !expected)
    throw Error("--apply requires the fingerprint from a dry run");
  const connectionString = process.env.DATABASE_URL;
  if (!connectionString) throw Error("DATABASE_URL is required");
  const ssl =
    process.env.DB_SSL === "true" ||
    process.env.DB_TARGET === "aiven" ||
    connectionString.includes("sslmode=require");
  const db = new Client({
    connectionString,
    connectionTimeoutMillis: 15000,
    ...(ssl ? { ssl: { rejectUnauthorized: false } } : {}),
  });
  await db.connect();
  try {
    await db.query(
      apply ? "BEGIN ISOLATION LEVEL SERIALIZABLE" : "BEGIN READ ONLY",
    );
    const versions = new Set(
      (
        await db.query("SELECT version FROM nexusport_schema_versions")
      ).rows.map((r) => r.version),
    );
    if (apply) {
      await db.query("SET LOCAL lock_timeout='10s'");
      // Acquire the strongest DDL locks first, without waiting while holding a
      // partial lock set. This avoids upgrades deadlocking with live readers.
      if (!versions.has("20261104_030"))
        await db.query(
          "LOCK TABLE yard_blocks,yard_slots IN ACCESS EXCLUSIVE MODE NOWAIT",
        );
      await db.query(
        `LOCK TABLE ${tables.join(",")} IN SHARE ROW EXCLUSIVE MODE NOWAIT`,
      );
    }
    const migration = "20261104_030_tien_sa_layout_metadata.sql";
    const missing = fs
      .readdirSync(path.join(__dirname, "../migrations"))
      .filter(
        (f) =>
          f.endsWith(".sql") &&
          f !== migration &&
          !versions.has(f.slice(0, 12)) &&
          !(
            f === "20261012_005_reefer_power_management.sql" &&
            versions.has("005")
          ),
      );
    if (missing.length)
      throw Error("Missing prerequisite migrations: " + missing.join(", "));
    const state = await readState(db),
      active = state.smart_yard_maps.filter((m) => m.is_active);
    if (active.length !== 1) throw Error("Exactly one active map is required");
    if (active[0].version === design.version) {
      console.log(
        JSON.stringify({ alreadyApplied: true, version: design.version }),
      );
      await db.query("ROLLBACK");
      return;
    }
    if (active[0].version.startsWith("tien-sa-"))
      throw Error(
        "An existing Tiên Sa deployment needs a separate upgrade plan",
      );
    if (
      state.yard_blocks.some((b) => design.areas.some((a) => a.code === b.code))
    )
      throw Error(
        "A target block code already exists; explicit reconciliation is required",
      );
    const live = state.smart_yard_conditions.filter(
      (c) =>
        c.map_id === active[0].id &&
        (!c.valid_until || new Date(c.valid_until) > new Date()) &&
        (c.restricted || Number(c.congestion) > 0),
    );
    if (live.length)
      throw Error("Live road conditions need mapping before publication");
    if (state.yard_slot_reservations.some((r) => r.reserved_to === null))
      throw Error("Existing open reservations need explicit reconciliation");
    const plan = buildRelayoutPlan(state, design),
      token = fingerprint(state);
    const report = {
      fingerprint: token,
      previousMap: active[0].version,
      nextMap: design.version,
      ...plan.summary,
      allocations: plan.entities.map((e) => ({
        container: e.containerNo,
        size: e.size,
        location: `${e.slot.block}-${e.slot.bay}-${e.slot.row}-${e.slot.tier}`,
        physical: !!e.position,
        bookings: e.bookings.map((l) => l.booking.booking_code),
        powerSessions: e.power.length,
      })),
    };
    if (!apply) {
      console.log(JSON.stringify(report, null, 2));
      await db.query("ROLLBACK");
      return;
    }
    if (token !== expected)
      throw Error("Data changed after dry run; review a new plan");
    const backupDir = path.resolve(__dirname, "../../../db-backups");
    fs.mkdirSync(backupDir, { recursive: true });
    const backup = path.join(
      backupDir,
      "tien-sa-" + new Date().toISOString().replace(/[:.]/g, "-") + ".json",
    );
    fs.writeFileSync(
      backup,
      JSON.stringify({ fingerprint: token, report, state }, null, 2),
      { flag: "wx" },
    );
    if (!versions.has("20261104_030"))
      await db.query(
        fs
          .readFileSync(
            path.join(__dirname, "../migrations", migration),
            "utf8",
          )
          .replace(/^BEGIN;\s*/, "")
          .replace(/COMMIT;\s*$/, ""),
      );
    const capacity = new Map();
    for (const s of plan.slots)
      capacity.set(s.blockId, (capacity.get(s.blockId) || 0) + 1);
    for (const a of plan.areas)
      await db.query(
        `INSERT INTO yard_blocks(id,code,name,zone,max_capacity,current_occupancy,is_reefer_area,is_active,layout_version,bay_modules,row_count,tier_count)
   VALUES($1,$2,$3,$4,$5,0,$6,true,$7,$8,$9,$10)`,
        [
          a.id,
          a.code,
          "Block " + a.code,
          a.sector,
          capacity.get(a.id),
          a.sector === "D",
          design.version,
          a.planning.bayModules,
          a.planning.rows,
          a.planning.tiers,
        ],
      );
    // Bulk insertion still invokes the footprint guard for every configured envelope.
    await db.query(
      `INSERT INTO yard_slots(id,block_id,bay,row_no,tier,status,has_reefer_plug,container_length_ft,module_start,module_span)
   SELECT id,"blockId",bay,"row",tier,status::yard_slot_status,"hasReeferPlug","containerLengthFt","moduleStart","moduleSpan"
   FROM jsonb_to_recordset($1::jsonb) AS x(id uuid,"blockId" uuid,bay int,"row" int,tier int,status text,"hasReeferPlug" boolean,"containerLengthFt" int,"moduleStart" int,"moduleSpan" int)`,
      [JSON.stringify(plan.slots)],
    );
    const openIds = state.bookings.filter(openBooking).map((b) => b.id);
    for (const e of plan.entities) {
      if (e.position) {
        await db.query(
          "UPDATE container_positions SET is_current=false,removed_at=now() WHERE id=$1 AND is_current",
          [e.position.id],
        );
        await db.query(
          "INSERT INTO container_positions(container_id,slot_id,placed_by,placed_at) VALUES($1,$2,$3,now())",
          [e.containerId, e.slot.id, e.position.placed_by],
        );
      }
      const operations = [];
      for (const l of e.bookings) {
        const b = l.booking,
          candidates = state.yard_tasks.filter(
            (t) =>
              t.booking_id === b.id &&
              t.ContainerId === e.containerId &&
              !t.IsDeleted &&
              !["completed", "canceled", "cancelled"].includes(
                String(t.Status).toLowerCase(),
              ),
          );
        if (candidates.length > 1)
          throw Error("Multiple live operations for one booking/container");
        const id =
            candidates[0]?.Id ||
            stableId("operation:" + b.id + ":" + e.containerId),
          location = `${e.slot.block}-${e.slot.bay}-${e.slot.row}-${e.slot.tier}`;
        if (candidates.length)
          await db.query(
            `UPDATE yard_tasks SET "ToLocation"=$2,"BlockCode"=$3,"UpdatedAt"=now(),"UpdatedBy"='Tiên Sa relayout' WHERE "Id"=$1`,
            [id, location, e.slot.block],
          );
        else
          await db.query(
            `INSERT INTO yard_tasks("Id","TaskCode","OperationType","ContainerId","ContainerNo","ContainerType","DriverId","ToLocation","BlockCode","Status","AssignedAt","AssignedBy","CreatedBy",booking_id,container_visit_id)
     VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,'Assigned',now(),'Tiên Sa relayout','Tiên Sa relayout',$10,$11)`,
            [
              id,
              "TS-" + id.slice(0, 8),
              l.direction === "pickup" ? "Pickup" : "Dropoff",
              e.containerId,
              e.containerNo,
              e.typeCode,
              b.driver_id,
              location,
              e.slot.block,
              b.id,
              l.container_visit_id,
            ],
          );
        operations.push(id);
      }
      if (!e.position)
        await db.query(
          `INSERT INTO yard_slot_reservations(slot_id,container_id,reserved_from,reason) VALUES($1,$2,now(),$3)`,
          [
            e.slot.id,
            e.containerId,
            operations[0]
              ? "NXP-059 operation:" + operations[0]
              : "Tiên Sa power allocation:" + e.key,
          ],
        );
    }
    for (const p of plan.plugAssignments)
      await db.query("UPDATE reefer_plugs SET slot_id=$2 WHERE id=$1", [
        p.plugId,
        p.slotId,
      ]);
    // Keep closed journeys and receipt references intact; a changed active destination
    // requires a fresh arrival confirmation, rather than projecting its old waypoint.
    await db.query(
      `UPDATE driver_route_progress p SET current_node_id=NULL,arrived_at=NULL,destination_key='',updated_at=now()
   FROM gate_transactions gt WHERE gt.id=p.gate_transaction_id AND gt.booking_id=ANY($1::uuid[])`,
      [openIds],
    );
    await db.query(`UPDATE yard_tasks SET "IsDeleted"=true,"UpdatedAt"=now(),"UpdatedBy"='Tiên Sa relayout: archived unlinked legacy sample'
   WHERE "ContainerId" IS NULL AND booking_id IS NULL AND NOT "IsDeleted" AND lower("Status") NOT IN ('completed','canceled','cancelled')`);
    const retired = state.yard_blocks.map((b) => b.id);
    await db.query(
      `UPDATE yard_slots s SET status='empty',has_reefer_plug=false WHERE block_id=ANY($1::uuid[]) AND status IN ('occupied','reserved')
   AND NOT EXISTS(SELECT 1 FROM container_positions p WHERE p.slot_id=s.id AND p.is_current)
   AND NOT EXISTS(SELECT 1 FROM yard_slot_reservations r WHERE r.slot_id=s.id AND r.reserved_to IS NULL)`,
      [retired],
    );
    await db.query(
      "UPDATE yard_blocks SET is_active=false,current_occupancy=0 WHERE id=ANY($1::uuid[])",
      [retired],
    );
    await db.query(
      `UPDATE yard_blocks b SET current_occupancy=(SELECT count(*) FROM container_positions p JOIN yard_slots s ON s.id=p.slot_id WHERE s.block_id=b.id AND p.is_current) WHERE layout_version=$1`,
      [design.version],
    );
    const map = {
      version: design.version,
      nodes: design.nodes,
      edges: design.edges,
      layout: {
        drawingVersion: design.version,
        blocks: plan.areas.map((a) => ({
          code: a.code,
          label: a.code,
          x: a.x,
          y: a.y,
          width: a.width,
          height: a.height,
          bays: a.planning.bayModules * 2 - 1,
          rows: a.planning.rows,
          tiers: a.planning.tiers,
        })),
        slots: [],
      },
    };
    await db.query(
      "UPDATE smart_yard_maps SET is_active=false WHERE is_active",
    );
    await db.query(
      `INSERT INTO smart_yard_maps(version,name,map_data,is_active) VALUES($1,'Tiên Sa · basic yard corridors',$2,true)`,
      [design.version, JSON.stringify(map)],
    );
    const count = (
      await db.query(
        "SELECT count(*)::int AS n FROM container_positions WHERE is_current",
      )
    ).rows[0].n;
    if (count !== plan.summary.physicalContainers)
      throw Error("Physical placement count changed unexpectedly");
    const overlap = (
      await db.query(`SELECT 1 FROM yard_slots a JOIN yard_slots b ON a.id<b.id AND a.block_id=b.block_id AND a.row_no=b.row_no AND a.tier=b.tier
   WHERE a.module_start<b.module_start+b.module_span AND b.module_start<a.module_start+a.module_span LIMIT 1`)
    ).rows.length;
    if (overlap) throw Error("Configured slot footprints overlap");
    await db.query("COMMIT");
    console.log(JSON.stringify({ ...report, applied: true, backup }, null, 2));
  } catch (error) {
    await db.query("ROLLBACK");
    throw error;
  } finally {
    await db.end();
  }
}
if (require.main === module)
  main().catch((e) => {
    console.error(
      JSON.stringify({
        code: e.code,
        message: e.message,
        detail: e.detail,
        where: e.where,
      }),
    );
    process.exitCode = 1;
  });
module.exports = { readState, fingerprint };

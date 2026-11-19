"use strict";

const referenceLayout = require("../../../node-core/src/shared/yard/reference-layout.json");
const terminalPlan = require("../../../node-core/src/shared/yard/tien-sa-plan.json");

// Codes are matched exactly. A numbered physical block is not silently treated as
// a bay of another block; missing reference positions remain unconfigured.
function positionKey(slot) {
  return `${slot.block}:${Number(slot.bay)}:${Number(slot.row)}`;
}

function buildLayoutSnapshot(rows, generatedAt = new Date().toISOString()) {
  const currentDesign = rows[0]?.drawingVersion === terminalPlan.version;
  const positions = new Set(
    referenceLayout.areas.flatMap((area) =>
      area.rows.map((row) => `${area.zone}:${area.bay}:${row}`),
    ),
  );
  const blocks = new Map();
  const slots = [];
  for (const row of rows) {
    if (!blocks.has(row.block))
      blocks.set(row.block, {
        id: row.blockId,
        code: row.block,
        name: row.blockName,
      });
    if (!row.id) continue;
    const isGatedOut = row.isGatedOut === 1 || row.isGatedOut === true;
    const isOccupiedOrReserved = !isGatedOut && (row.status === 'occupied' || row.status === 'reserved' || row.status === 'in_yard');
    const containerNumber = isOccupiedOrReserved ? (row.containerNumber || row.reservedContainerNumber || null) : null;
    const containerId = (isOccupiedOrReserved && containerNumber)
      ? (row.containerId || `res-${row.id}`)
      : null;
    const finalStatus = isGatedOut ? 'empty' : (isOccupiedOrReserved ? row.status : 'empty');

    slots.push({
      id: row.id,
      blockId: row.blockId,
      block: row.block,
      bay: Number(row.bay),
      row: Number(row.row),
      tier: Number(row.tier),
      status: finalStatus,
      containerId: containerId,
      containerNumber: containerNumber,
      moduleStart: row.moduleStart == null ? null : Number(row.moduleStart),
      moduleSpan: row.moduleSpan == null ? null : Number(row.moduleSpan),
      containerLengthFt:
        row.containerLengthFt == null ? null : Number(row.containerLengthFt),
      hasReeferPlug: row.hasReeferPlug === true,
      reservedContainerNumber: isOccupiedOrReserved ? (row.reservedContainerNumber || null) : null,
      bookingCode: isOccupiedOrReserved ? (row.bookingCode || null) : null,
      vehiclePlate: isOccupiedOrReserved ? (row.vehiclePlate || null) : null,
      driverName: isOccupiedOrReserved ? (row.driverName || null) : null,
      company: isOccupiedOrReserved ? (row.company || null) : null,
      sealNumber: isOccupiedOrReserved ? (row.sealNumber || null) : null,
      operationType: isOccupiedOrReserved ? (row.operationType || null) : null,
      equipmentCode: isOccupiedOrReserved ? (row.equipmentCode || null) : null,
      operatorName: isOccupiedOrReserved ? (row.operatorName || null) : null,
      notes: isOccupiedOrReserved ? (row.notes || null) : null,
      mapped: currentDesign
        ? terminalPlan.areas.some(
            (a) =>
              a.code === row.block &&
              Number(row.bay) > 0 &&
              Number(row.bay) < a.planning.bayModules * 2 &&
              Number(row.row) > 0 &&
              Number(row.row) <= a.planning.rows &&
              Number(row.tier) > 0 &&
              Number(row.tier) <= a.planning.tiers,
          )
        : positions.has(positionKey(row)),
    });
  }
  return {
    referenceLayout: {
      ...referenceLayout,
      status:
        rows[0]?.drawingVersion === referenceLayout.version
          ? "active"
          : "proposed",
    },
    terminalPlan: currentDesign ? { version: terminalPlan.version } : null,
    generatedAt,
    blocks: [...blocks.values()],
    slots,
    unmappedSlotCount: slots.filter((slot) => !slot.mapped).length,
  };
}

async function getLayoutSnapshot() {
  const { sequelize } = require("../../config/database");
  // One statement gives consistent occupancy and physical container positions.
  const [rows] = await sequelize.query(`
    SELECT yb.id AS "blockId", yb.code AS block, yb.name AS "blockName",
      ys.id, ys.bay, ys.row_no AS row, ys.tier,
      COALESCE(
        CASE WHEN gor.is_gated_out IS NOT NULL THEN 'empty' END,
        CASE WHEN pyr.status IS NOT NULL THEN pyr.status END,
        CASE WHEN cp.container_id IS NOT NULL THEN 'occupied' END,
        CASE WHEN ysr.reservation_id IS NOT NULL THEN (CASE WHEN ys.status::text = 'occupied' THEN 'occupied' ELSE 'reserved' END) END,
        ys.status::text
      ) AS status,
      ys.module_start AS "moduleStart",ys.module_span AS "moduleSpan",ys.container_length_ft AS "containerLengthFt",
      ys.has_reefer_plug AS "hasReeferPlug",
      cp.container_id AS "containerId",
      COALESCE(
        c.container_no,
        yt."ContainerNo",
        pyr.container_no,
        ysr.container_no
      ) AS "containerNumber",
      COALESCE(
        pyr.container_no,
        ysr.container_no,
        yt."ContainerNo"
      ) AS "reservedContainerNumber",
      COALESCE(pyr.booking_code, bk.booking_code, yt."TaskCode") AS "bookingCode",
      COALESCE(bk.vehicle_plate, yt."VehiclePlate") AS "vehiclePlate",
      COALESCE(bk.driver_name, yt."DriverName") AS "driverName",
      COALESCE(carr.company_name, yt."OperationType") AS "company",
      COALESCE(c.seal_no, yt."ExpectedSealNo", yt."ActualSealNo") AS "sealNumber",
      yt."OperationType" AS "operationType",
      yt."EquipmentCode" AS "equipmentCode",
      yt."OperatorName" AS "operatorName",
      yt."Notes" AS "notes",
      gor.is_gated_out AS "isGatedOut",
      (SELECT map_data->'layout'->>'drawingVersion' FROM smart_yard_maps WHERE is_active=true ORDER BY updated_at DESC LIMIT 1) AS "drawingVersion"
    FROM yard_blocks yb
    LEFT JOIN yard_slots ys ON ys.block_id=yb.id
    LEFT JOIN LATERAL (
      SELECT container_no, booking_code, status
      FROM persistent_yard_reservations
      WHERE (block_code=yb.code OR block_code=REPLACE(yb.code, 'Block ', ''))
        AND bay=ys.bay AND row_no=ys.row_no AND tier=ys.tier
      ORDER BY updated_at DESC LIMIT 1
    ) pyr ON true
    LEFT JOIN LATERAL (
      SELECT rc.container_no, r.id AS reservation_id
      FROM yard_slot_reservations r
      JOIN containers rc ON rc.id=r.container_id
      WHERE r.slot_id=ys.id AND r.reserved_to IS NULL
      ORDER BY r.reserved_from DESC LIMIT 1
    ) ysr ON true
    LEFT JOIN LATERAL (
      SELECT container_id FROM container_positions
      WHERE slot_id=ys.id AND is_current=true ORDER BY placed_at DESC LIMIT 1
    ) cp ON true
    LEFT JOIN containers c ON c.id=cp.container_id
    LEFT JOIN LATERAL (
      SELECT * FROM yard_tasks
      WHERE ("IsDeleted" IS NOT TRUE OR "IsDeleted" IS NULL)
        AND ("Status" NOT IN ('GatedOut', 'Cancelled'))
        AND ("ToLocation" = yb.code || '-' || LPAD(ys.bay::text, 2, '0') || '-' || LPAD(ys.row_no::text, 2, '0') || '-' || ys.tier::text
          OR "ToLocation" = yb.code || '-' || ys.bay::text || '-' || ys.row_no::text || '-' || ys.tier::text
          OR "ToLocation" = yb.code || '.' || LPAD(ys.bay::text, 2, '0') || '.' || LPAD(ys.row_no::text, 2, '0') || '.' || ys.tier::text
          OR "ToLocation" = yb.code || '.' || ys.bay::text || '.' || ys.row_no::text || '.' || ys.tier::text)
      ORDER BY "CreatedAt" DESC LIMIT 1
    ) yt ON true
    LEFT JOIN LATERAL (
      SELECT 1 AS is_gated_out
      FROM yard_gate_out_requests ygr
      WHERE UPPER(TRIM(ygr.container_id)) = UPPER(TRIM(COALESCE(c.container_no, yt."ContainerNo", pyr.container_no, ysr.container_no)))
        AND (ygr.status = 'COMPLETED' OR ygr.flow_step >= 5)
      LIMIT 1
    ) gor ON true
    LEFT JOIN LATERAL (
      SELECT b.booking_code, b.vehicle_plate, b.driver_name, b.carrier_id, b.status AS booking_status
      FROM bookings b
      WHERE (b.id::text = ys.id::text 
         OR (c.id IS NOT NULL AND EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.booking_id=b.id AND bc.container_id=c.id)))
      ORDER BY b.created_at DESC LIMIT 1
    ) bk ON true
    LEFT JOIN carriers carr ON carr.id = bk.carrier_id
    WHERE yb.is_active=true
    ORDER BY yb.code,ys.bay,ys.row_no,ys.tier`);
  return buildLayoutSnapshot(rows);
}

function blockDirectory(snapshot) {
  return snapshot.blocks.map((block) => {
    const slots = snapshot.slots.filter((slot) => slot.block === block.code);
    return {
      id: block.id,
      blockCode: block.code,
      description: block.name,
      slots,
      maxBays: Math.max(0, ...slots.map((slot) => slot.bay)),
      maxRows: Math.max(0, ...slots.map((slot) => slot.row)),
      maxTiers: Math.max(0, ...slots.map((slot) => slot.tier)),
    };
  });
}
module.exports = {
  referenceLayout,
  buildLayoutSnapshot,
  getLayoutSnapshot,
  blockDirectory,
};

'use strict';

const referenceLayout = require('../../../node-core/src/shared/yard/reference-layout.json');

// Codes are matched exactly. A numbered physical block is not silently treated as
// a bay of another block; missing reference positions remain unconfigured.
function positionKey(slot) {
  return `${slot.block}:${Number(slot.bay)}:${Number(slot.row)}`;
}

function buildLayoutSnapshot(rows, generatedAt = new Date().toISOString()) {
  const positions = new Set(referenceLayout.areas.flatMap(area =>
    area.rows.map(row => `${area.zone}:${area.bay}:${row}`)));
  const blocks = new Map();
  const slots = [];
  for (const row of rows) {
    if (!blocks.has(row.block)) blocks.set(row.block, { id: row.blockId, code: row.block, name: row.blockName });
    if (!row.id) continue;
    slots.push({
      id: row.id, blockId: row.blockId, block: row.block,
      bay: Number(row.bay), row: Number(row.row), tier: Number(row.tier),
      status: row.status, containerId: row.containerId || null,
      containerNumber: row.containerNumber || null,
      mapped: positions.has(positionKey(row)),
    });
  }
  return {
    referenceLayout: { ...referenceLayout, status: rows[0]?.drawingVersion === referenceLayout.version ? 'active' : 'proposed' }, generatedAt, blocks: [...blocks.values()], slots,
    unmappedSlotCount: slots.filter(slot => !slot.mapped).length,
  };
}

async function getLayoutSnapshot() {
  const { sequelize } = require('../../config/database');
  // One statement gives consistent occupancy and physical container positions.
  const [rows] = await sequelize.query(`
    SELECT yb.id AS "blockId", yb.code AS block, yb.name AS "blockName",
      ys.id, ys.bay, ys.row_no AS row, ys.tier, ys.status::text AS status,
      cp.container_id AS "containerId", c.container_no AS "containerNumber",
      (SELECT map_data->'layout'->>'drawingVersion' FROM smart_yard_maps WHERE is_active=true ORDER BY updated_at DESC LIMIT 1) AS "drawingVersion"
    FROM yard_blocks yb
    LEFT JOIN yard_slots ys ON ys.block_id=yb.id
    LEFT JOIN LATERAL (
      SELECT container_id FROM container_positions
      WHERE slot_id=ys.id AND is_current=true ORDER BY placed_at DESC LIMIT 1
    ) cp ON true
    LEFT JOIN containers c ON c.id=cp.container_id
    ORDER BY yb.code,ys.bay,ys.row_no,ys.tier`);
  return buildLayoutSnapshot(rows);
}

function blockDirectory(snapshot) {
  return snapshot.blocks.map(block=>{
    const slots=snapshot.slots.filter(slot=>slot.block===block.code);
    return {id:block.id,blockCode:block.code,description:block.name,slots,maxBays:Math.max(0,...slots.map(slot=>slot.bay)),maxRows:Math.max(0,...slots.map(slot=>slot.row)),maxTiers:Math.max(0,...slots.map(slot=>slot.tier))};
  });
}
module.exports = { referenceLayout, buildLayoutSnapshot, getLayoutSnapshot, blockDirectory };

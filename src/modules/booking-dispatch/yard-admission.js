'use strict';
const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const { fail } = require('./dispatch.policy');
const read = (sql, replacements = {}, transaction) => sequelize.query(sql, {
  replacements, transaction, type: QueryTypes.SELECT, logging: false,
});
const capacity = () => {
  const value = Number(process.env.YARD_VEHICLE_CAPACITY || 6);
  return Number.isInteger(value) && value > 0 && value <= 1000 ? value : 6;
};
const automatic = () => process.env.YARD_AUTO_ADMISSION !== 'false';
// A booking occupies one place throughout invitation -> passage -> Gate Out.
// The UNION also prevents a consumed invitation being counted a second time.
const occupancySql = `WITH inside AS (
  SELECT DISTINCT g.booking_id FROM gate_transactions g WHERE g.gate_type='gate_in' AND g.status='completed'
  AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=g.booking_id
    AND x.gate_type='gate_out' AND x.status='completed' AND x.checked_at>=g.checked_at)
), reserved AS (
  SELECT DISTINCT e.booking_id FROM gate_entry_orders e JOIN bookings b ON b.id=e.booking_id
  WHERE e.status IN ('pending_ack','active') AND e.valid_until>now() AND b.status::text IN ('approved','ready')
), occupied AS (SELECT booking_id FROM inside UNION SELECT booking_id FROM reserved)
SELECT (SELECT count(*)::int FROM inside) AS inside,
  (SELECT count(*)::int FROM occupied) AS occupied`;
async function snapshot(transaction) {
  const [counts] = await read(occupancySql, {}, transaction);
  const limit = capacity(), inside = Number(counts.inside), occupied = Number(counts.occupied);
  return { capacity: limit, inside, reserved: occupied - inside, available: Math.max(0, limit - occupied), automatic: automatic() };
}
async function lock(transaction) {
  // Every manual/automatic call takes this lock before the booking/fleet locks.
  await read('SELECT pg_advisory_xact_lock(114, 6)', {}, transaction);
}
async function requireSpace(transaction) {
  if (!(await snapshot(transaction)).available) throw fail('Bãi đã đủ sức chứa hoặc đã giữ chỗ cho xe được gọi. Vui lòng chờ xe Gate Out.');
}
async function assignDestination(bookingId, transaction) {
  // Demo reception queues are separate from physical container slots. Assignment
  // never moves a container, reserves a storage slot or completes a Yard task.
  const locations = (process.env.YARD_DEMO_HANDOVER_LOCATIONS || 'A-S1,A-S2').split(',').map(v => v.trim()).filter(Boolean);
  if (!locations.length) return false;
  const [point] = await read(`SELECT n->>'location' AS location FROM smart_yard_maps m,
    LATERAL jsonb_array_elements(m.map_data->'nodes') n
    WHERE m.is_active AND n->>'location' IN (:locations) AND NOT COALESCE((n->>'restricted')::boolean,false)
    AND (m.map_data->>'validUntil' IS NULL OR (m.map_data->>'validUntil')::timestamptz>now())
    AND NOT EXISTS(SELECT 1 FROM smart_yard_conditions sc WHERE sc.map_id=m.id AND sc.target_type='node'
      AND sc.target_id=n->>'id' AND sc.restricted AND (sc.valid_until IS NULL OR sc.valid_until>now()))
    AND NOT (n ? 'slotId') AND NOT (n ? 'gateCode')
    ORDER BY (SELECT count(DISTINCT ri.booking_id) FROM driver_route_instructions ri JOIN bookings b ON b.id=ri.booking_id
      WHERE ri.map_data->>'source'='demo_admission' AND ri.map_data->>'handoverLocation'=n->>'location'
      AND b.status::text IN ('approved','ready','checked_in','completed')
      AND NOT EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_out' AND g.status='completed')),
      n->>'location' LIMIT 1`, { locations }, transaction);
  if (!point) return false;
  await read(`INSERT INTO driver_route_instructions(booking_id,driver_id,route_detail,map_data)
    SELECT b.id,b.driver_id,'Đi tới điểm giao nhận được phân công; xác nhận trên app khi đã đến.',
      jsonb_build_object('source','demo_admission','containerId',bc.container_id::text,'handoverLocation',:location)
    FROM bookings b JOIN booking_containers bc ON bc.booking_id=b.id WHERE b.id=:bookingId
    AND NOT EXISTS(SELECT 1 FROM driver_route_instructions ri WHERE ri.booking_id=b.id AND ri.driver_id=b.driver_id
      AND ri.map_data->>'containerId'=bc.container_id::text AND ri.map_data->>'source'='demo_admission')
    RETURNING id`, { bookingId, location: point.location }, transaction);
  return true;
}
async function runAutoAdmission() {
  if (!automatic()) return;
  // Repair missing demo directions for trips already inside, without inventing
  // arrival/handover events. Real Yard destinations always take precedence.
  const inside = await read(`SELECT DISTINCT g.booking_id FROM gate_transactions g JOIN bookings b ON b.id=g.booking_id
    WHERE g.gate_type='gate_in' AND g.status='completed' AND b.status::text='checked_in'
    AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=b.id AND x.gate_type='gate_out' AND x.status='completed')
    AND EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.booking_id=b.id AND NOT EXISTS(
      SELECT 1 FROM driver_route_instructions ri WHERE ri.booking_id=b.id AND ri.driver_id=b.driver_id
      AND ri.map_data->>'containerId'=bc.container_id::text AND ri.map_data->>'source'='demo_admission')) LIMIT 30`);
  for (const item of inside) await sequelize.transaction({ logging: false }, async transaction => {
    await lock(transaction);
    await read('SELECT id FROM bookings WHERE id=:bookingId FOR UPDATE', { bookingId: item.booking_id }, transaction);
    await assignDestination(item.booking_id, transaction);
  });
  if (!(await snapshot()).available) return;
  const candidates = await read(`SELECT b.id,s.version FROM bookings b JOIN booking_dispatch_state s ON s.booking_id=b.id
    WHERE b.status::text IN ('approved','ready') AND s.status='ready' AND (s.hold_until IS NULL OR s.hold_until<=now())
    AND b.appointment_start<=now()+interval '12 hours' AND s.action NOT IN ('call_expired','entry_expired','released')
    ORDER BY (s.arrived_at>b.appointment_start+interval '10 minutes'),b.appointment_start,b.created_at LIMIT 30`);
  // Reuse the version/ownership/fleet checks and durable notification transaction.
  const { act } = require('./dispatch.service');
  for (const item of candidates) {
    try { await act(null, 'Dispatcher', item.id, 'call', { expectedVersion: item.version, note: 'Bãi còn sức chứa. Xác nhận nhận lệnh để tới cổng vào; sau Gate In, đi theo điểm giao nhận trên app.' }); }
    catch (error) { if (error.statusCode !== 409) throw error; }
    if (!(await snapshot()).available) break;
  }
}
module.exports = { snapshot, lock, requireSpace, assignDestination, runAutoAdmission, occupancySql };

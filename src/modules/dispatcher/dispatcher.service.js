'use strict';

/**
 * Số liệu thật cho trang tổng quan Dispatcher: lệnh bãi, phương tiện, thiết bị, công suất bãi, sự cố.
 * Mọi con số truy vấn trực tiếp từ database (không có dữ liệu mẫu).
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

const run = (sql, replacements = {}) => sequelize.query(sql, { replacements, type: QueryTypes.SELECT });
const safe = async (promise, fallback) => { try { return await promise; } catch (err) { console.warn('[Dispatcher] Truy vấn lỗi:', err.message); return fallback; } };
const TZ = 'Asia/Ho_Chi_Minh';

async function getOverview() {
  const [taskStatus, taskQueue, recentDone, equipment, blocks, vehicles, arrivals, incidents, gateToday, bookingsToday, conflicts] = await Promise.all([
    safe(run(`SELECT "Status" AS status, count(*)::int AS n FROM yard_tasks WHERE "IsDeleted" = false GROUP BY 1`), []),
    safe(run(`SELECT "Id" AS id, "TaskCode" AS code, "OperationType" AS type, "ContainerNo" AS container, "ContainerType" AS "containerType",
                     "FromLocation" AS "from", "ToLocation" AS "to", "BlockCode" AS block, "Priority" AS priority, "Status" AS status,
                     "EquipmentCode" AS equipment, "OperatorName" AS operator, "VehiclePlate" AS plate, "DriverName" AS driver,
                     "DueTime" AS "dueTime", "CreatedAt" AS "createdAt"
                FROM yard_tasks
               WHERE "IsDeleted" = false AND "Status" NOT IN ('Completed', 'Cancelled', 'Done', 'Failed')
               ORDER BY CASE "Priority" WHEN 'Critical' THEN 0 WHEN 'High' THEN 1 WHEN 'Medium' THEN 2 ELSE 3 END, "DueTime" NULLS LAST, "CreatedAt" LIMIT 8`), []),
    safe(run(`SELECT "TaskCode" AS code, "OperationType" AS type, "ContainerNo" AS container, "CompletedLocation" AS location, "EquipmentCode" AS equipment,
                     COALESCE("EndTime", "UpdatedAt") AS "doneAt"
                FROM yard_tasks WHERE "IsDeleted" = false AND "Status" IN ('Completed', 'Done')
               ORDER BY COALESCE("EndTime", "UpdatedAt") DESC NULLS LAST LIMIT 6`), []),
    safe(run(`SELECT "Id" AS id, "EquipmentCode" AS code, "Name" AS name, "EquipmentType" AS type, "BlockCode" AS block, "Status" AS status, "OperatorName" AS operator
                FROM "Equipments" WHERE "IsDeleted" = false ORDER BY "EquipmentType", "EquipmentCode"`), []),
    safe(run(`SELECT yb.code AS block, COALESCE(yb.is_reefer_area, false) AS reefer, count(ys.id)::int AS total,
                     count(*) FILTER (WHERE ys.status::text IN ('occupied', 'reserved'))::int AS used,
                     count(*) FILTER (WHERE ys.status::text = 'occupied')::int AS occupied
                FROM yard_blocks yb LEFT JOIN yard_slots ys ON ys.block_id = yb.id GROUP BY yb.code, yb.is_reefer_area
               HAVING count(ys.id) > 0 ORDER BY yb.code`), []),
    safe(run(`SELECT b.id, b.booking_code AS code, b.booking_type::text AS type, b.status::text AS status, b.trip_status::text AS "tripStatus",
                     COALESCE(b.vehicle_plate, t.plate_number) AS plate, COALESCE(b.driver_name, d.full_name) AS driver, b.appointment_start AS "appointmentStart",
                     ca.company_name AS carrier
                FROM bookings b LEFT JOIN trucks t ON t.id = b.truck_id LEFT JOIN drivers d ON d.id = b.driver_id LEFT JOIN carriers ca ON ca.id = b.carrier_id
               WHERE b.status::text = 'checked_in' ORDER BY b.updated_at DESC LIMIT 10`), []),
    safe(run(`SELECT b.id, b.booking_code AS code, b.booking_type::text AS type, b.status::text AS status, COALESCE(b.vehicle_plate, t.plate_number) AS plate,
                     COALESCE(b.driver_name, d.full_name) AS driver, b.appointment_start AS "appointmentStart", ca.company_name AS carrier
                FROM bookings b LEFT JOIN trucks t ON t.id = b.truck_id LEFT JOIN drivers d ON d.id = b.driver_id LEFT JOIN carriers ca ON ca.id = b.carrier_id
               WHERE b.status::text IN ('approved', 'ready') AND b.appointment_start >= now() - interval '2 hours'
               ORDER BY b.appointment_start LIMIT 8`), []),
    safe(run(`SELECT id, code, incident_type AS type, severity, location, container_no AS container, status, reported_at AS "reportedAt"
                FROM yard_incident_reports WHERE status <> 'RESOLVED'
               ORDER BY CASE severity WHEN 'CRITICAL' THEN 0 WHEN 'HIGH' THEN 1 WHEN 'MEDIUM' THEN 2 ELSE 3 END, reported_at DESC LIMIT 6`), []),
    safe(run(`SELECT count(*) FILTER (WHERE gate_type = 'gate_in' AND status = 'completed')::int AS gate_in,
                     count(*) FILTER (WHERE gate_type = 'gate_out' AND status = 'completed')::int AS gate_out
                FROM gate_transactions WHERE (checked_at AT TIME ZONE '${TZ}')::date = (now() AT TIME ZONE '${TZ}')::date`), [{ gate_in: 0, gate_out: 0 }]),
    safe(run(`SELECT count(*)::int AS n FROM bookings WHERE status::text IN ('approved', 'ready') AND (appointment_start AT TIME ZONE '${TZ}')::date = (now() AT TIME ZONE '${TZ}')::date`), [{ n: 0 }]),
    safe(run(`SELECT count(*)::int AS n FROM yard_incident_reports WHERE status <> 'RESOLVED' AND details->>'kind' = 'SLOT_CONFLICT'`), [{ n: 0 }]),
  ]);

  const byStatus = Object.fromEntries(taskStatus.map((r) => [r.status, r.n]));
  const totalTasks = taskStatus.reduce((s, r) => s + r.n, 0);
  const completed = (byStatus.Completed || 0) + (byStatus.Done || 0);
  const closed = completed + (byStatus.Cancelled || 0) + (byStatus.Failed || 0);
  const inProgress = byStatus.InProgress || byStatus['In Progress'] || byStatus.Started || 0;

  const totalSlots = blocks.reduce((s, b) => s + b.total, 0);
  const usedSlots = blocks.reduce((s, b) => s + b.used, 0);
  const eq = { total: equipment.length, available: 0, working: 0, maintenance: 0 };
  equipment.forEach((e) => { if (eq[e.status] !== undefined) eq[e.status] += 1; });

  return {
    generatedAt: new Date().toISOString(),
    tasks: {
      total: totalTasks,
      open: totalTasks - closed,
      unstarted: totalTasks - closed - inProgress,
      inProgress,
      completed,
      byStatus: taskStatus,
      queue: taskQueue,
      recentDone,
      critical: taskQueue.filter((t) => t.priority === 'Critical').length,
    },
    vehicles: { inPort: vehicles.length, list: vehicles, arrivals, arrivalsToday: bookingsToday[0]?.n || 0 },
    gate: gateToday[0],
    equipment: { ...eq, list: equipment },
    yard: { totalSlots, usedSlots, utilization: totalSlots ? Math.round((usedSlots / totalSlots) * 100) : 0, blocks },
    incidents: { open: incidents.length, list: incidents },
    conflicts: conflicts[0]?.n || 0,
  };
}

module.exports = { getOverview };

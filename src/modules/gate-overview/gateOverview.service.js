'use strict';

/**
 * Số liệu thật cho trang tổng quan của nhân viên cổng: lượt qua cổng, xe đang trong cảng,
 * lịch hẹn sắp đến, trạng thái từng cổng và các lượt bị chặn. Không có dữ liệu mẫu.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

const run = (sql, replacements = {}) => sequelize.query(sql, { replacements, type: QueryTypes.SELECT });
const safe = async (promise, fallback) => { try { return await promise; } catch (err) { console.warn('[GateOverview] Truy vấn lỗi:', err.message); return fallback; } };
const TZ = 'Asia/Ho_Chi_Minh';
const TODAY = `(now() AT TIME ZONE '${TZ}')::date`;

async function getOverview() {
  const [totals, gates, inPort, upcoming, overdue, recent, failures, hourly] = await Promise.all([
    safe(run(`SELECT count(*) FILTER (WHERE gate_type = 'gate_in' AND status = 'completed')::int AS "gateIn",
                     count(*) FILTER (WHERE gate_type = 'gate_out' AND status = 'completed')::int AS "gateOut",
                     count(*) FILTER (WHERE status NOT IN ('completed', 'pending'))::int AS blocked,
                     count(*) FILTER (WHERE status = 'pending')::int AS pending
                FROM gate_transactions WHERE (checked_at AT TIME ZONE '${TZ}')::date = ${TODAY}`), [{ gateIn: 0, gateOut: 0, blocked: 0, pending: 0 }]),
    safe(run(`SELECT g.id, g.code, g.name, g.location, g.is_active AS "isActive",
                     count(t.id) FILTER (WHERE t.gate_type = 'gate_in' AND t.status = 'completed')::int AS "gateIn",
                     count(t.id) FILTER (WHERE t.gate_type = 'gate_out' AND t.status = 'completed')::int AS "gateOut",
                     count(t.id) FILTER (WHERE t.status NOT IN ('completed', 'pending'))::int AS blocked,
                     max(t.checked_at) AS "lastActivityAt"
                FROM gates g LEFT JOIN gate_transactions t ON t.gate_id = g.id AND (t.checked_at AT TIME ZONE '${TZ}')::date = ${TODAY}
               GROUP BY g.id ORDER BY g.is_active DESC, g.code`), []),
    safe(run(`SELECT b.id, b.booking_code AS code, b.booking_type::text AS type, COALESCE(b.vehicle_plate, t.plate_number) AS plate,
                     COALESCE(b.driver_name, d.full_name) AS driver, ca.company_name AS carrier, b.updated_at AS "since"
                FROM bookings b LEFT JOIN trucks t ON t.id = b.truck_id LEFT JOIN drivers d ON d.id = b.driver_id LEFT JOIN carriers ca ON ca.id = b.carrier_id
               WHERE b.status::text = 'checked_in' ORDER BY b.updated_at DESC LIMIT 12`), []),
    safe(run(`SELECT b.id, b.booking_code AS code, b.booking_type::text AS type, b.status::text AS status, COALESCE(b.vehicle_plate, t.plate_number) AS plate,
                     COALESCE(b.driver_name, d.full_name) AS driver, ca.company_name AS carrier, b.appointment_start AS "appointmentStart", b.appointment_end AS "appointmentEnd"
                FROM bookings b LEFT JOIN trucks t ON t.id = b.truck_id LEFT JOIN drivers d ON d.id = b.driver_id LEFT JOIN carriers ca ON ca.id = b.carrier_id
               WHERE b.status::text IN ('approved', 'ready') AND b.appointment_end >= now() - interval '1 hour' AND b.appointment_start <= now() + interval '24 hours'
               ORDER BY b.appointment_start LIMIT 12`), []),
    safe(run(`SELECT count(*)::int AS n FROM bookings WHERE status::text IN ('approved', 'ready') AND appointment_end < now() AND appointment_end >= now() - interval '7 days'`), [{ n: 0 }]),
    safe(run(`SELECT t.id, t.gate_type AS "gateType", t.status, t.plate_number_detected AS plate, t.container_number AS container, t.block_reason AS "blockReason",
                     t.checked_at AS "checkedAt", g.name AS "gateName", t.eir_number AS eir
                FROM gate_transactions t LEFT JOIN gates g ON g.id = t.gate_id ORDER BY t.checked_at DESC NULLS LAST LIMIT 10`), []),
    safe(run(`SELECT id, gate_code AS "gateCode", status, failure_reason AS reason, verified_at AS "verifiedAt", COALESCE(vehicle_plate, detected_plate) AS plate, driver_name AS driver
                FROM gate_verification_records WHERE status::text !~* '^(pass|success|ok|verified|completed)' ORDER BY verified_at DESC LIMIT 6`), []),
    safe(run(`SELECT extract(hour FROM checked_at AT TIME ZONE '${TZ}')::int AS hour,
                     count(*) FILTER (WHERE gate_type = 'gate_in' AND status = 'completed')::int AS "gateIn",
                     count(*) FILTER (WHERE gate_type = 'gate_out' AND status = 'completed')::int AS "gateOut"
                FROM gate_transactions WHERE (checked_at AT TIME ZONE '${TZ}')::date = ${TODAY} GROUP BY 1 ORDER BY 1`), []),
  ]);

  return {
    generatedAt: new Date().toISOString(),
    today: totals[0],
    inPort: { count: inPort.length, list: inPort },
    upcoming: { count: upcoming.length, list: upcoming },
    overdue: overdue[0]?.n || 0,
    gates,
    recent,
    failures,
    hourly,
  };
}

module.exports = { getOverview };

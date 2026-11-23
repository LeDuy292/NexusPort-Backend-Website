'use strict';

/**
 * Lịch sử điều phối của Dispatcher, dựng từ dữ liệu thật (không còn mock):
 *  - Lệnh xe qua cổng: bảng bookings + trạng thái điều phối (booking_dispatch_state) + giao dịch cổng (gate_transactions).
 *  - Lệnh nội bãi: bảng yard_tasks không gắn booking (đảo / dời container trong bãi).
 * Trạng thái quy đổi: COMPLETED | CANCELLED | DELAYED (quá hạn lịch hẹn mà chưa xong) | IN_PROGRESS.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

const MAX_DAYS = 90;
const iso = (v) => (v ? new Date(v).toISOString() : null);
const query = (sql, replacements = {}) => sequelize.query(sql, { replacements, type: QueryTypes.SELECT });

function bookingStatus(row, now) {
  if (['canceled', 'rejected', 'expired'].includes(row.status)) return 'CANCELLED';
  if (row.status === 'completed' || row.gateOutAt) return 'COMPLETED';
  const deadline = row.appointmentEnd ? new Date(row.appointmentEnd).getTime() : null;
  return deadline && now > deadline ? 'DELAYED' : 'IN_PROGRESS';
}

function bookingTimeline(row) {
  const events = [
    [row.createdAt, 'Tạo lịch hẹn'],
    [row.approvedAt, 'Dispatcher duyệt lịch hẹn'],
    [row.arrivedAt, 'Tài xế báo đã đến cổng'],
    [row.readyAt, 'Tài xế xác nhận sẵn sàng'],
    [row.calledAt, 'Điều độ gọi xe vào cổng'],
    [row.acknowledgedAt, 'Tài xế nhận lệnh vào cổng'],
    [row.gateInAt, 'Xe qua cổng vào (Gate In)'],
    [row.gateOutAt, 'Xe qua cổng ra (Gate Out)'],
    [row.canceledAt, row.rejectedReason ? `Lịch hẹn bị hủy: ${row.rejectedReason}` : 'Lịch hẹn bị hủy'],
  ];
  return events.filter(([at]) => at).map(([at, event]) => ({ at: iso(at), event })).sort((a, b) => a.at.localeCompare(b.at));
}

async function getBookingRows(since) {
  return query(
    `SELECT b.id, b.booking_code AS "bookingCode", b.booking_type::text AS "bookingType", b.status::text AS status,
            b.appointment_start AS "appointmentStart", b.appointment_end AS "appointmentEnd", b.created_at AS "createdAt",
            b.approved_at AS "approvedAt", b.canceled_at AS "canceledAt", b.rejected_reason AS "rejectedReason",
            COALESCE(t.plate_number, b.vehicle_plate) AS plate, COALESCE(d.full_name, b.driver_name) AS "driverName",
            b.driver_id AS "driverId", ds.arrived_at AS "arrivedAt", ds.ready_at AS "readyAt", ds.called_at AS "calledAt",
            ds.acknowledged_at AS "acknowledgedAt", ds.updated_at AS "dispatchUpdatedAt", u.username AS dispatcher,
            gin.checked_at AS "gateInAt", gout.checked_at AS "gateOutAt", gin.gate_name AS "gateName",
            COALESCE((SELECT string_agg(c.container_no, ', ' ORDER BY c.container_no) FROM booking_containers bc
                       JOIN containers c ON c.id = bc.container_id WHERE bc.booking_id = b.id), '') AS containers,
            (SELECT yt."ToLocation" FROM yard_tasks yt WHERE yt.booking_id = b.id AND yt."IsDeleted" = false
              ORDER BY yt."CreatedAt" DESC LIMIT 1) AS "yardLocation"
       FROM bookings b
       LEFT JOIN drivers d ON d.id = b.driver_id
       LEFT JOIN trucks t ON t.id = b.truck_id
       LEFT JOIN booking_dispatch_state ds ON ds.booking_id = b.id
       LEFT JOIN users u ON u.id = ds.updated_by
       LEFT JOIN LATERAL (SELECT x.checked_at, g.name AS gate_name FROM gate_transactions x LEFT JOIN gates g ON g.id = x.gate_id
                           WHERE x.booking_id = b.id AND x.gate_type = 'gate_in' AND x.status = 'completed'
                           ORDER BY x.checked_at LIMIT 1) gin ON true
       LEFT JOIN LATERAL (SELECT x.checked_at FROM gate_transactions x
                           WHERE x.booking_id = b.id AND x.gate_type = 'gate_out' AND x.status = 'completed'
                           ORDER BY x.checked_at DESC LIMIT 1) gout ON true
      WHERE COALESCE(b.appointment_start, b.created_at) >= :since
      ORDER BY COALESCE(b.appointment_start, b.created_at) DESC
      LIMIT 500`,
    { since },
  );
}

async function getYardMoveRows(since) {
  return query(
    `SELECT "Id" AS id, "TaskCode" AS code, "OperationType" AS "operationType", "Status" AS status, "ContainerNo" AS container,
            "VehiclePlate" AS plate, "DriverName" AS "driverName", "FromLocation" AS origin, "ToLocation" AS destination,
            "CreatedAt" AS "createdAt", "StartTime" AS "startTime", "EndTime" AS "endTime", "CreatedBy" AS dispatcher
       FROM yard_tasks
      WHERE "IsDeleted" = false AND booking_id IS NULL AND "CreatedAt" >= :since
      ORDER BY "CreatedAt" DESC
      LIMIT 500`,
    { since },
  );
}

async function getHistory({ days } = {}) {
  const span = Math.min(Math.max(Number(days) || 30, 1), MAX_DAYS);
  const since = new Date(Date.now() - span * 24 * 3600 * 1000);
  const now = Date.now();
  const [bookings, moves] = await Promise.all([getBookingRows(since), getYardMoveRows(since)]);

  const items = bookings.map((r) => ({
    id: r.bookingCode,
    kind: r.bookingType === 'pickup' ? 'PORT_PICKUP' : 'DELIVERY',
    at: iso(r.appointmentStart || r.createdAt),
    plate: r.plate || null,
    driverName: r.driverName || null,
    driverId: r.driverId || null,
    container: r.containers || null,
    origin: r.bookingType === 'pickup' ? (r.yardLocation || 'Bãi cảng') : (r.gateName || 'Cổng cảng'),
    destination: r.bookingType === 'pickup' ? 'Cổng ra cảng' : (r.yardLocation || 'Bãi cảng'),
    dispatcher: r.dispatcher || null,
    status: bookingStatus(r, now),
    dispatchAt: iso(r.calledAt || r.approvedAt || r.createdAt),
    startAt: iso(r.acknowledgedAt),
    arrivalAt: iso(r.gateInAt),
    completionAt: iso(r.gateOutAt),
    waitMs: r.arrivedAt && r.calledAt ? new Date(r.calledAt) - new Date(r.arrivedAt) : null,
    callMs: r.readyAt && r.calledAt ? new Date(r.calledAt) - new Date(r.readyAt) : null,
    gateBooking: r.bookingCode,
    timeline: bookingTimeline(r),
  }));

  for (const m of moves) {
    const st = String(m.status || '').toLowerCase();
    const status = ['cancelled', 'canceled'].includes(st) ? 'CANCELLED' : st === 'completed' ? 'COMPLETED' : 'IN_PROGRESS';
    items.push({
      id: m.code || String(m.id),
      kind: 'YARD_MOVE',
      at: iso(m.createdAt),
      plate: m.plate || null,
      driverName: m.driverName || null,
      driverId: null,
      container: m.container || null,
      origin: m.origin || null,
      destination: m.destination || null,
      dispatcher: m.dispatcher || null,
      status,
      dispatchAt: iso(m.createdAt),
      startAt: iso(m.startTime),
      arrivalAt: null,
      completionAt: iso(m.endTime),
      waitMs: null,
      callMs: null,
      gateBooking: null,
      timeline: [
        { at: iso(m.createdAt), event: 'Tạo lệnh di chuyển' },
        m.startTime && { at: iso(m.startTime), event: 'Bắt đầu thực hiện' },
        m.endTime && { at: iso(m.endTime), event: status === 'CANCELLED' ? 'Lệnh bị hủy' : 'Hoàn thành' },
      ].filter(Boolean),
    });
  }

  items.sort((a, b) => String(b.at).localeCompare(String(a.at)));
  return { days: span, generatedAt: new Date().toISOString(), items };
}

module.exports = { getHistory };

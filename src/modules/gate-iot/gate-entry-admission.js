'use strict';

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const admission = require('../booking-dispatch/yard-admission');
const { fail } = require('../booking-dispatch/dispatch.policy');

const read = (sql, replacements, transaction) => sequelize.query(sql, {
  replacements, transaction, type: QueryTypes.SELECT, logging: false,
});
const normalizePlate = plate => String(plate || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
const entryGate = code => ['GATE-01', 'GATE_IN', 'GATE_IN_A'].includes(String(code || '').toUpperCase());

async function authorizeEntry(userId, { vehiclePlate, bookingCode, gateCode } = {}) {
  const plate = normalizePlate(vehiclePlate);
  if (!plate || plate.length > 20 || !entryGate(gateCode)) {
    throw fail('Cần biển số hợp lệ và cổng vào GATE-01.', 422);
  }
  const code = typeof bookingCode === 'string' ? bookingCode.trim().toUpperCase() : '';
  return sequelize.transaction({ logging: false }, async transaction => {
    // Dispatcher calls reserve from the same pool and take this lock first.
    await admission.lock(transaction);
    const [gate] = await read("SELECT id FROM gates WHERE code='GATE-01' AND is_active", {}, transaction);
    if (!gate) throw fail('Cổng vào GATE-01 đang ngừng hoạt động.');
    const candidates = await read(`SELECT b.id,b.booking_code,b.driver_id,b.truck_id,
        (now()>=b.appointment_start AND now()<b.appointment_end) AS on_time,
        (ds.hold_until>now()) AS held,
        (ds.status='arrived') AS not_ready,
        e.id AS order_id,e.valid_until,e.source
      FROM bookings b JOIN drivers d ON d.id=b.driver_id JOIN trucks t ON t.id=b.truck_id
      LEFT JOIN booking_dispatch_state ds ON ds.booking_id=b.id
      LEFT JOIN gate_entry_orders e ON e.booking_id=b.id AND e.status='active'
        AND now()>=e.valid_from AND now()<e.valid_until AND e.vehicle_plate=:plate
        AND e.driver_id=b.driver_id AND e.truck_id=b.truck_id
      WHERE b.status::text IN ('approved','ready')
        AND lower(replace(COALESCE(b.gate_type::text,''),'_',''))<>'gateout'
        AND d.status::text NOT IN ('inactive','banned') AND t.status::text NOT IN ('inactive','maintenance')
        AND upper(regexp_replace(COALESCE(t.plate_number,b.vehicle_plate,''),'[^a-zA-Z0-9]','','g'))=:plate
        AND (:code = '' OR upper(b.booking_code)=:code)
        AND NOT EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_in'
          AND g.status='completed')
      ORDER BY (e.id IS NOT NULL) DESC,on_time DESC NULLS LAST,b.appointment_start,b.id LIMIT 2
      FOR UPDATE OF b,d`, { plate, code }, transaction);
    const [booking, next] = candidates;
    if (!booking) throw fail('Không tìm thấy booking chờ vào khớp biển số.');
    if (next && (Boolean(booking.order_id) && next.order_id ||
      !booking.order_id && booking.on_time && next.on_time)) {
      throw fail('Có nhiều booking khớp biển số. Cần điều độ xác định đúng chuyến.');
    }
    if (booking.held || booking.not_ready) throw fail('Chuyến đang chờ điều độ hoặc chưa sẵn sàng.');
    if (booking.order_id) {
      return { bookingCode: booking.booking_code, orderId: booking.order_id, validUntil: booking.valid_until, source: booking.source };
    }
    if (!booking.on_time) {
      throw fail('Xe đến ngoài khung giờ booking. Tài xế cần báo đến khu chờ và nhận lệnh gọi của điều độ.');
    }
    const [other] = await read(`SELECT 1 FROM bookings b WHERE b.id<>:bookingId
      AND (b.driver_id=:driverId OR b.truck_id=:truckId)
      AND (EXISTS(SELECT 1 FROM gate_entry_orders e WHERE e.booking_id=b.id
        AND e.status IN ('pending_ack','active') AND e.valid_until>now() AND b.status::text IN ('approved','ready'))
      OR EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_in' AND g.status='completed'
        AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=b.id AND x.gate_type='gate_out'
          AND x.status='completed' AND x.checked_at>=g.checked_at))) LIMIT 1`, {
      bookingId: booking.id, driverId: booking.driver_id, truckId: booking.truck_id,
    }, transaction);
    if (other) throw fail('Xe hoặc tài xế đang có một lượt qua cổng khác chưa kết thúc.');
    const [reservation] = await read(`SELECT id FROM gate_entry_orders WHERE booking_id=:bookingId
      AND status='pending_ack' AND valid_until>now()`, { bookingId: booking.id }, transaction);
    if (!reservation) await admission.requireSpace(transaction);
    if (!await admission.assignDestination(booking.id, transaction)) {
      throw fail('Chưa có điểm giao nhận hợp lệ trên bản đồ bãi.');
    }
    await read(`INSERT INTO booking_dispatch_state
      (booking_id,driver_id,status,action,arrived_at,ready_at,message,updated_by,updated_at)
      VALUES(:bookingId,:driverId,'acknowledged','gate_admitted',now(),now(),
        'Cổng ghi nhận xe đến đúng giờ và đã cấp lệnh vào.',:userId,now())
      ON CONFLICT(booking_id) DO UPDATE SET driver_id=EXCLUDED.driver_id,status=EXCLUDED.status,
        action=EXCLUDED.action,arrived_at=CASE WHEN booking_dispatch_state.status='withdrawn' THEN EXCLUDED.arrived_at
          ELSE booking_dispatch_state.arrived_at END,
        ready_at=EXCLUDED.ready_at,called_at=NULL,call_expires_at=NULL,acknowledged_at=NULL,hold_until=NULL,
        message=EXCLUDED.message,updated_by=EXCLUDED.updated_by,updated_at=EXCLUDED.updated_at,
        version=booking_dispatch_state.version+1 RETURNING booking_id`, {
      bookingId: booking.id, driverId: booking.driver_id, userId,
    }, transaction);
    const [order] = await read(`SELECT id,valid_until,source FROM gate_entry_orders WHERE booking_id=:bookingId
      AND status='active' AND source='gate_scan'`, { bookingId: booking.id }, transaction);
    if (!order) throw fail('Cổng chưa cấp được lệnh vào. Vui lòng xác minh lại.');
    return { bookingCode: booking.booking_code, orderId: order.id, validUntil: order.valid_until, source: order.source };
  });
}

async function claimInboundHardwareOpen(commandId) {
  const [command] = await read(`UPDATE gate_barrier_commands c SET hardware_requested_at=clock_timestamp()
    FROM gates g
    WHERE c.id=:commandId AND c.gate_code='GATE-01' AND c.action='open' AND c.status IN ('simulated','acknowledged')
      AND c.hardware_requested_at IS NULL AND c.requested_at>now()-interval '2 minutes'
      AND c.gate_id=g.id AND g.is_active
      AND (
        (c.source='manual' AND c.verification_id IS NULL AND c.entry_order_id IS NULL)
        OR EXISTS(
          SELECT 1 FROM gate_entry_orders e JOIN bookings b ON b.id=e.booking_id
          JOIN gate_verification_records v ON v.booking_id=b.id
          WHERE c.entry_order_id=e.id AND e.status='active' AND now()>=e.valid_from AND now()<e.valid_until
            AND e.driver_id=b.driver_id AND e.truck_id=b.truck_id AND b.status::text IN ('approved','ready')
            AND v.id=c.verification_id AND v.status='PASS' AND v.gate_code=c.gate_code
        )
      ) RETURNING c.id`, { commandId });
  return Boolean(command);
}

module.exports = { authorizeEntry, claimInboundHardwareOpen, entryGate, normalizePlate };

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

/**
 * Ghi nhận xe đã qua cổng vào (Gate In) cho xe đang có lệnh vào cổng còn hiệu lực.
 *  - Cổng mở tự động: không chỉ định xe → lấy xe điều độ gọi SỚM NHẤT (xếp hàng đến trước vào trước).
 *  - Tài xế tự xác nhận: chỉ định bookingId + tài khoản tài xế, chỉ ghi cho đúng lịch hẹn của họ.
 * Ghi gate_transactions (trigger sẽ cập nhật chuyến vận chuyển), tiêu thụ lệnh vào cổng, chuyển lịch hẹn sang đã vào cổng.
 */
async function recordEntryPassage({ bookingId = null, driverUserId = null, source = 'auto' } = {}) {
  return sequelize.transaction({ logging: false }, async transaction => {
    await admission.lock(transaction);
    const [gate] = await read("SELECT id FROM gates WHERE code='GATE-01' AND is_active", {}, transaction);
    if (!gate) throw fail('Cổng vào GATE-01 đang ngừng hoạt động.');
    const [entry] = await read(`SELECT e.id AS order_id, e.status AS order_status, e.vehicle_plate, b.id AS booking_id, b.booking_code,
        b.driver_id, b.truck_id, d.user_id AS driver_user_id
      FROM gate_entry_orders e JOIN bookings b ON b.id=e.booking_id JOIN drivers d ON d.id=b.driver_id
      WHERE e.status IN ('active','pending_ack') AND now()>=e.valid_from AND now()<e.valid_until
        AND b.status::text IN ('approved','ready')
        AND NOT EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_in' AND g.status='completed')
        AND (CAST(:bookingId AS uuid) IS NULL OR b.id=CAST(:bookingId AS uuid))
        AND (CAST(:driverUserId AS uuid) IS NULL OR d.user_id=CAST(:driverUserId AS uuid))
      ORDER BY e.issued_at ASC, e.id
      LIMIT 1 FOR UPDATE OF e,b`, { bookingId, driverUserId }, transaction);
    if (!entry) {
      throw fail(driverUserId
        ? 'Bạn chưa có lệnh vào cổng còn hiệu lực. Hãy chờ điều độ gọi hoặc quét tại cổng.'
        : 'Không có xe nào đang được điều độ gọi vào cổng.', 409);
    }
    const [container] = await read(`SELECT ct.container_no FROM booking_containers bc JOIN containers ct ON ct.id=bc.container_id
      WHERE bc.booking_id=:bookingId ORDER BY ct.container_no LIMIT 1`, { bookingId: entry.booking_id }, transaction);
    const [tx] = await read(`INSERT INTO gate_transactions(id,booking_id,gate_id,driver_id,truck_id,gate_type,status,plate_number_detected,
        checked_at,completed_at,container_number)
      VALUES(gen_random_uuid(),:bookingId,:gateId,:driverId,:truckId,'gate_in','completed',:plate,now(),now(),:container) RETURNING id`, {
      bookingId: entry.booking_id, gateId: gate.id, driverId: entry.driver_id, truckId: entry.truck_id,
      plate: entry.vehicle_plate, container: container?.container_no || null,
    }, transaction);
    await read(`UPDATE gate_entry_orders SET status='consumed',consumed_at=now(),acknowledged_at=COALESCE(acknowledged_at,now()),
        gate_transaction_id=:txId WHERE id=:orderId RETURNING id`, { txId: tx.id, orderId: entry.order_id }, transaction);
    await read("UPDATE bookings SET status='checked_in',trip_status='Arrived',updated_at=now() WHERE id=:id RETURNING id",
      { id: entry.booking_id }, transaction);
    await read(`INSERT INTO driver_booking_notifications(user_id,booking_id,event_key,title,message)
      VALUES(:userId,:bookingId,:key,'Xe đã qua cổng vào',:message) ON CONFLICT DO NOTHING RETURNING id`, {
      userId: entry.driver_user_id, bookingId: entry.booking_id, key: 'gate_in:' + tx.id,
      message: 'Lịch hẹn ' + entry.booking_code + ': ' + (source === 'driver' ? 'bạn đã xác nhận qua cổng vào.' : 'cổng đã ghi nhận xe qua cổng vào.') + ' Làm theo hướng dẫn trong cảng.',
    }, transaction);
    return { bookingId: entry.booking_id, bookingCode: entry.booking_code, vehiclePlate: entry.vehicle_plate, gateTransactionId: tx.id, source };
  });
}

/**
 * Lượt quét camera: khi lệnh MỞ cổng đã được chấp nhận (barie mở) cho một lệnh vào cổng còn hiệu lực và đã qua thời gian xe
 * đi qua (GATE_OPEN_SETTLE_SECONDS, mặc định 10 giây) mà cổng chưa bị đóng thủ công, hệ thống coi xe đã qua cổng:
 * ghi Gate In và chuyển trạng thái chuyến / lịch hẹn. Idempotent: xe đã có Gate In thì bỏ qua.
 */
async function settleOpenedGates() {
  const settle = Math.min(120, Math.max(3, Number(process.env.GATE_OPEN_SETTLE_SECONDS || 10) || 10));
  const opened = await read(`SELECT DISTINCT ON (e.booking_id) e.booking_id
    FROM gate_barrier_commands c JOIN gate_entry_orders e ON e.id=c.entry_order_id
    WHERE c.action='open' AND c.status IN ('simulated','acknowledged') AND c.gate_code='GATE-01'
      AND c.requested_at<=now()-CAST(:settle AS integer) * interval '1 second' AND c.requested_at>now()-interval '10 minutes'
      AND e.status IN ('active','pending_ack') AND c.requested_at>=e.valid_from
      AND NOT EXISTS(SELECT 1 FROM gate_barrier_commands x WHERE x.gate_code=c.gate_code AND x.action='close' AND x.requested_at>c.requested_at)
      AND NOT EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=e.booking_id AND g.gate_type='gate_in' AND g.status='completed')
    ORDER BY e.booking_id,c.requested_at LIMIT 10`, { settle });
  for (const item of opened) {
    try { await recordEntryPassage({ bookingId: item.booking_id, source: 'gate-open' }); }
    catch (error) { if (error.statusCode !== 409) throw error; }
  }
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

module.exports = { authorizeEntry, claimInboundHardwareOpen, entryGate, normalizePlate, recordEntryPassage, settleOpenedGates };

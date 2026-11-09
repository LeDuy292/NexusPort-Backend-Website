'use strict';
const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const { BOOKING_FIELDS } = require('../driver-bookings/driver-bookings.service');
const { POLICY, fail, activeBooking, decorate, checkArrival, checkCall, expectedVersion } = require('./dispatch.policy');
const admission = require('./yard-admission');
const query = (sql, replacements = {}, transaction) => sequelize.query(sql, { replacements, transaction, type: QueryTypes.SELECT });

async function getBoard(date) {
  if (typeof date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(date) || !Number.isFinite(Date.parse(date))
    || new Date(date).toISOString().slice(0,10) !== date) {
    throw fail('Ngày điều phối không hợp lệ (YYYY-MM-DD).', 422);
  }
  const bookings = await query(BOOKING_FIELDS + `
    WHERE b.status::text IN ('approved','ready') AND
      ((b.appointment_start < ((CAST(:date AS date)+1)::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh')
        AND b.appointment_end > (CAST(:date AS date)::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh'))
       OR (ds.status IN ('arrived','ready','called','acknowledged') AND ds.updated_at>now()-interval '24 hours'))
    ORDER BY b.appointment_start,b.created_at LIMIT 300`, { date });
  const now = new Date();
  const items = bookings.map(b => decorate(b, now));
  items.sort((a, b) => Number(b.coordination.canCall) - Number(a.coordination.canCall)
    || Number(a.coordination.isLate || a.dispatch?.action==='call_expired') - Number(b.coordination.isLate || b.dispatch?.action==='call_expired')
    || Date.parse(a.appointmentStart) - Date.parse(b.appointmentStart)
    || Date.parse(a.createdAt) - Date.parse(b.createdAt));
  return { bookings: items, serverTime: now.toISOString(), policy: POLICY, yard: await admission.snapshot() };
}

async function act(userId, role, bookingId, action, input = {}) {
  const version = expectedVersion(input.expectedVersion);
  return sequelize.transaction(async transaction => {
    if (action === 'call') await admission.lock(transaction);
    // Serialize by booking and fleet driver, including concurrent dispatchers.
    const rows = await query(`SELECT b.id,d.user_id AS "userId" FROM bookings b
      JOIN drivers d ON d.id=b.driver_id WHERE b.id=:bookingId `
      + (role === 'Driver' ? 'AND d.user_id=:userId ' : '') + 'FOR UPDATE OF b,d', { bookingId, userId }, transaction);
    if (!rows.length) throw fail('Không tìm thấy booking được giao cho bạn.', 404);
    const [raw] = await query(BOOKING_FIELDS + ' WHERE b.id=:bookingId', { bookingId }, transaction);
    if (!activeBooking(raw)) throw fail('Booking chưa được duyệt hoặc không còn chờ điều phối.');
    const current = raw.dispatch;
    if ((current?.version || 0) !== version) throw fail('Điều phối vừa thay đổi. Vui lòng tải lại trước khi thao tác.');
    const now = new Date();
    const state = current?.status === 'withdrawn' ? null : current;
    let status, message = state?.message || null, holdUntil = state?.holdUntil || null;
    let arrivedAt = state?.arrivedAt || now, readyAt = state?.readyAt || null;
    let calledAt = state?.calledAt || null, callExpiresAt = state?.callExpiresAt || null;
    let acknowledgedAt = state?.acknowledgedAt || null;
    if (role === 'Driver') {
      if (action === 'arrive') {
        checkArrival(raw, now);
        if (state) throw fail('Bạn đã xác nhận đến cho booking này.');
        status = 'ready'; readyAt = now;
        message = 'Tài xế xác nhận đã đến khu chờ và sẵn sàng.';
      } else if (action === 'not-ready' && state && ['ready','arrived'].includes(state.status)) {
        status = 'arrived'; readyAt = null;
      } else if (action === 'ready' && state?.status === 'arrived') {
        status = 'ready'; readyAt = now;
      } else if (action === 'acknowledge' && decorate(raw, now).coordination.canAcknowledge) {
        status = 'acknowledged'; acknowledgedAt = now;
        message = 'Tài xế đã nhận lệnh gọi. Chờ hướng dẫn và kiểm tra tại cổng.';
      } else throw fail('Không thể thực hiện thao tác ở trạng thái hiện tại.');
    } else {
      if (!['Dispatcher','Administrator'].includes(role)) throw fail('Không có quyền điều phối.', 403);
      const note = typeof input.note === 'string' ? input.note.trim() : '';
      if (note.length > 500) throw fail('Hướng dẫn tối đa 500 ký tự.', 422);
      if (action === 'call') {
        checkCall(raw, now);
        await admission.requireSpace(transaction);
        // Truck lock prevents two different drivers being called with the same truck.
        await query('SELECT id FROM trucks WHERE id=(SELECT truck_id FROM bookings WHERE id=:bookingId) FOR UPDATE', {bookingId}, transaction);
        const others = await query(`SELECT s.booking_id FROM booking_dispatch_state s JOIN bookings b ON b.id=s.booking_id
          WHERE s.booking_id<>:bookingId AND s.status IN ('called','acknowledged')
          AND b.status::text IN ('approved','ready') AND (s.driver_id=:driverId OR
            (b.truck_id IS NOT NULL AND b.truck_id=(SELECT truck_id FROM bookings WHERE id=:bookingId)))
          UNION ALL SELECT b.id FROM bookings b WHERE b.id<>:bookingId
          AND (b.driver_id=:driverId OR (b.truck_id IS NOT NULL AND b.truck_id=(SELECT truck_id FROM bookings WHERE id=:bookingId)))
          AND EXISTS(SELECT 1 FROM gate_transactions g WHERE g.booking_id=b.id AND g.gate_type='gate_in' AND g.status='completed'
            AND NOT EXISTS(SELECT 1 FROM gate_transactions x WHERE x.booking_id=b.id AND x.gate_type='gate_out'
              AND x.status='completed' AND x.checked_at>=g.checked_at))
          LIMIT 1`, { bookingId, driverId: raw.driverId }, transaction);
        if (others.length) throw fail('Tài xế hoặc xe đang có một lệnh gọi khác chưa kết thúc.');
        if (!await admission.assignDestination(bookingId, transaction)) throw fail('Chưa có điểm giao nhận trên bản đồ bãi. Vui lòng kiểm tra cấu hình trước khi gọi xe.');
        status = 'called'; calledAt = now; acknowledgedAt = null; holdUntil = null;
        callExpiresAt = new Date(now.getTime() + POLICY.callResponseMinutes * 60000);
        message = note || 'Điều độ đã gọi bạn. Xác nhận nhận lệnh trong 10 phút và làm theo hướng dẫn của nhân viên cổng.';
      } else if (action === 'delay' && state && ['ready','called','acknowledged'].includes(state.status)) {
        if (!Number.isInteger(input.minutes) || input.minutes < 1 || input.minutes > 120 || !note) {
          throw fail('Nhập lý do và thời gian chờ từ 1 đến 120 phút.', 422);
        }
        status = 'ready'; holdUntil = new Date(now.getTime() + input.minutes * 60000);
        calledAt = null; callExpiresAt = null; acknowledgedAt = null;
        message = 'Tiếp tục chờ ' + input.minutes + ' phút. ' + note;
      } else if (action === 'release' && state && (['called','acknowledged'].includes(state.status) || state.holdUntil)) {
        status = 'ready'; holdUntil = null; calledAt = null; callExpiresAt = null; acknowledgedAt = null;
        message = note || 'Tiếp tục chờ tại khu chờ. Điều độ sẽ gửi lệnh gọi mới.';
      } else throw fail('Không thể thực hiện thao tác ở trạng thái hiện tại.');
    }
    const eventAction = {call:'called',arrive:'arrived',acknowledge:'acknowledged',delay:'delayed',release:'released'}[action] || action;
    // DB trigger persists notification and outbox in this transaction.
    await query(`INSERT INTO booking_dispatch_state
      (booking_id,driver_id,status,action,arrived_at,ready_at,called_at,call_expires_at,acknowledged_at,hold_until,message,version,updated_by,updated_at)
      VALUES(:bookingId,:driverId,:status,:eventAction,:arrivedAt,:readyAt,:calledAt,:callExpiresAt,:acknowledgedAt,:holdUntil,:message,:nextVersion,:userId,:now)
      ON CONFLICT(booking_id) DO UPDATE SET driver_id=EXCLUDED.driver_id,status=EXCLUDED.status,action=EXCLUDED.action,
        arrived_at=EXCLUDED.arrived_at,ready_at=EXCLUDED.ready_at,called_at=EXCLUDED.called_at,
        call_expires_at=EXCLUDED.call_expires_at,acknowledged_at=EXCLUDED.acknowledged_at,hold_until=EXCLUDED.hold_until,
        message=EXCLUDED.message,version=EXCLUDED.version,updated_by=EXCLUDED.updated_by,updated_at=EXCLUDED.updated_at
      RETURNING booking_id`, {
      bookingId, driverId: raw.driverId, status, eventAction, arrivedAt, readyAt, calledAt, callExpiresAt,
      acknowledgedAt, holdUntil, message, nextVersion: version + 1, userId, now,
    }, transaction);
    const [updated] = await query(BOOKING_FIELDS + ' WHERE b.id=:bookingId', { bookingId }, transaction);
    return decorate(updated);
  });
}
module.exports = { getBoard, act };

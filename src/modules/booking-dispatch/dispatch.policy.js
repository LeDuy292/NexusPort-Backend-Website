'use strict';
const POLICY = { earlyArrivalMinutes: 30, lateGraceMinutes: 10, callResponseMinutes: 10 };
const fail = (message, statusCode = 409) => Object.assign(new Error(message), { statusCode });
const activeBooking = booking => ['approved', 'ready'].includes(booking.status.toLowerCase());
function checkArrival(booking, now = new Date()) {
  if (!activeBooking(booking)) throw fail('Booking chưa được duyệt hoặc không còn chờ điều phối.');
  if (now.getTime() < Date.parse(booking.appointmentStart) - POLICY.earlyArrivalMinutes * 60000) {
    throw fail('Chỉ xác nhận đến trong vòng 30 phút trước lịch hẹn trở đi.');
  }
}
function decorate(booking, now = new Date()) {
  const dispatch = booking.dispatch;
  const active = activeBooking(booking);
  const effective = dispatch?.status === 'withdrawn' ? null : dispatch;
  const start = Date.parse(booking.appointmentStart);
  const time = now.getTime();
  const lateAt = effective?.arrivedAt ? Date.parse(effective.arrivedAt) : time;
  const isLate = lateAt > start + POLICY.lateGraceMinutes * 60000;
  const held = effective?.holdUntil && Date.parse(effective.holdUntil) > time;
  return { ...booking, dispatch, coordination: {
    isLate,
    canArrive: active && !effective && time >= start - POLICY.earlyArrivalMinutes * 60000,
    canCall: Boolean(active && effective?.status === 'ready' && !held && time >= start),
    canAcknowledge: Boolean(active && effective?.status === 'called' && Date.parse(effective.callExpiresAt) > time),
    isHeld: Boolean(held),
  } };
}
function checkCall(booking, now = new Date()) {
  if (!decorate(booking, now).coordination.canCall) {
    throw fail('Chỉ gọi xe đã sẵn sàng, tới giờ hẹn và không trong thời gian chờ thêm.');
  }
}
function expectedVersion(input) {
  if (!Number.isInteger(input) || input < 0) throw fail('Thiếu phiên bản điều phối. Vui lòng tải lại.', 422);
  return input;
}
module.exports = { POLICY, fail, activeBooking, decorate, checkArrival, checkCall, expectedVersion };

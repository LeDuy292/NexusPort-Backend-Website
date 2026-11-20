'use strict';

const UUID = /^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i;
const normalizeContainer = value => String(value || '').trim().toUpperCase();
const appointmentMs = value => {
  if (typeof value !== 'string') return NaN;
  const match = /^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)(Z|[+-]\d{2}:\d{2})?$/i.exec(value.trim());
  if (!match) return NaN;
  const day = Date.parse(match[1]);
  if (!Number.isFinite(day) || new Date(day).toISOString().slice(0, 10) !== match[1]) return NaN;
  if (match[2].startsWith('24:')) return NaN;
  return Date.parse(`${match[1]}T${match[2]}${match[3] || '+07:00'}`);
};

function windowFrom(startValue, endValue, defaultMinutes) {
  const start = appointmentMs(startValue);
  const end = endValue ? appointmentMs(endValue) : start + defaultMinutes * 60000;
  return Number.isFinite(start) && Number.isFinite(end) && end > start
    ? { start: new Date(start), end: new Date(end) } : null;
}

function registeredAppointment(snapshot, containerNumber) {
  const target = normalizeContainer(containerNumber);
  const { lines, delivery } = snapshot?.content || {};
  if (!target || !Array.isArray(lines) || !Array.isArray(delivery?.trips)) return null;
  const matchingLines = lines.filter(line => line?.sourceContainerId &&
    normalizeContainer(line.declaration?.containerNumber || line.containerNumber) === target);
  if (matchingLines.length !== 1) return null;
  const trips = delivery.trips.filter(trip => trip?.sourceContainerId === matchingLines[0].sourceContainerId);
  if (trips.length !== 1) return null;
  return windowFrom(trips[0].appointmentStart, trips[0].appointmentEnd);
}

async function getNewManifestAppointment(booking, query) {
  if (booking.appointmentStart) return windowFrom(booking.appointmentStart, booking.appointmentEnd, 90);
  if (typeof booking.eportOrderId === 'string' && UUID.test(booking.eportOrderId)) {
    const orders = await query(`SELECT snapshot FROM eport_service_registrations WHERE id=:orderId OR draft_id=:orderId`,
      { orderId: booking.eportOrderId });
    if (orders.length === 1) {
      const appointment = registeredAppointment(orders[0].snapshot, booking.containerId || booking.containerNumber);
      if (appointment) return appointment;
    }
  }
  return windowFrom(booking.eta, booking.appointmentEnd, 90);
}

module.exports = { getNewManifestAppointment };

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
  const containers = Array.isArray(snapshot?.containers) ? snapshot.containers : [];
  if (!target || !Array.isArray(delivery?.trips)) return null;

  const matchedIds = new Set();
  if (Array.isArray(lines)) {
    for (const line of lines) {
      if (line?.sourceContainerId &&
        normalizeContainer(line.declaration?.containerNumber || line.containerNumber) === target) {
        matchedIds.add(String(line.sourceContainerId));
      }
    }
  }
  for (const cont of containers) {
    const scId = cont?.sourceContainerId || cont?.id;
    if (scId && normalizeContainer(cont.containerNumber || cont.containerNo) === target) {
      matchedIds.add(String(scId));
    }
  }

  let trips = [];
  if (matchedIds.size === 1) {
    const [sourceId] = matchedIds;
    trips = delivery.trips.filter(trip => String(trip?.sourceContainerId || '') === sourceId);
  }
  if (!trips.length) {
    trips = delivery.trips.filter(trip => normalizeContainer(trip?.containerNumber || trip?.containerNo) === target);
  }
  if (!trips.length && delivery.trips.length === 1 && (containers.length === 1 || (Array.isArray(lines) && lines.length === 1))) {
    const onlyCont = normalizeContainer(
      containers[0]?.containerNumber || lines?.[0]?.declaration?.containerNumber || lines?.[0]?.containerNumber
    );
    if (onlyCont === target) trips = delivery.trips;
  }
  if (trips.length !== 1) return null;
  return windowFrom(trips[0].appointmentStart || snapshot?.content?.plannedAt, trips[0].appointmentEnd, 90);
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

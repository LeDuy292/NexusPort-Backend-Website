'use strict';

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const { BOOKING_FIELDS } = require('../driver-bookings/driver-bookings.service');
const { POLICY, decorate, fail } = require('./dispatch.policy');
const { syncManifestGateBookings } = require('./dispatch.service');
const admission = require('./yard-admission');

const FILTERS = ['all', 'waiting', 'called', 'inside', 'exited', 'absent'];
const PAGE_SIZE = 3;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const read = (sql, replacements) => sequelize.query(sql, { replacements, type: QueryTypes.SELECT });

// Match the latest completed Gate In of the assigned driver to its Gate Out.
// This classifies the trip without loading its container routes or yard map.
const gateJoins = `
  LEFT JOIN LATERAL (
    SELECT g.checked_at FROM gate_transactions g
    WHERE g.booking_id=b.id AND g.driver_id=b.driver_id AND g.gate_type='gate_in' AND g.status='completed'
    ORDER BY g.checked_at DESC,g.id DESC LIMIT 1
  ) gate_in ON true
  LEFT JOIN LATERAL (
    SELECT g.checked_at FROM gate_transactions g
    WHERE g.booking_id=b.id AND g.gate_type='gate_out' AND g.status='completed' AND g.checked_at>=gate_in.checked_at
    ORDER BY g.checked_at DESC,g.id DESC LIMIT 1
  ) gate_out ON true`;
const tripGroup = `CASE WHEN gate_in.checked_at IS NULL THEN 'queue'
  WHEN gate_out.checked_at IS NULL THEN 'inside' ELSE 'exited' END`;
const activeQueue = "b.status IN ('approved','ready')";
const dayStart = "(CAST(:date AS date)::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh')";
const dayEnd = "((CAST(:date AS date)+1)::timestamp AT TIME ZONE 'Asia/Ho_Chi_Minh')";
const queueAtDate = `${activeQueue} AND
  ((b.appointment_start<=${dayEnd} + (:early * interval '1 minute') AND b.appointment_end>${dayStart})
   OR EXISTS (
     SELECT 1 FROM gate_entry_orders e WHERE e.booking_id=b.id AND e.status IN ('pending_ack','active')
       AND e.valid_until>CAST(:now AS timestamptz) AND e.valid_until>${dayStart} AND e.issued_at<${dayEnd}
   ))`;
const visibleGateTrip = `gate_in.checked_at IS NOT NULL AND b.status NOT IN ('canceled','rejected','expired')
  AND (gate_out.checked_at IS NULL OR gate_out.checked_at>CAST(:now AS timestamptz)-interval '24 hours')`;
const filtersSql = `(:filter = 'all' OR :filter = trip_group OR (trip_group='queue' AND
  CASE :filter WHEN 'waiting' THEN dispatch_status IN ('ready','arrived')
    WHEN 'called' THEN dispatch_status IN ('called','acknowledged')
    WHEN 'absent' THEN dispatch_status='not-arrived' ELSE false END))`;

function validateDate(date) {
  if (typeof date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(date) || !Number.isFinite(Date.parse(date))
    || new Date(date).toISOString().slice(0, 10) !== date) {
    throw fail('Ngày điều phối không hợp lệ (YYYY-MM-DD).', 422);
  }
}

function tripScope(includeGateTrips, queueCondition) {
  return includeGateTrips ? {
    group: tripGroup, joins: gateJoins, condition: `(${visibleGateTrip}) OR (gate_in.checked_at IS NULL AND ${queueCondition})`,
  } : { group: "'queue'", joins: '', condition: queueCondition };
}

async function getBoard(date, { page: rawPage = '1', filter = 'all' } = {}, includeGateTrips = true) {
  validateDate(date);
  if (typeof rawPage !== 'string' || !/^[1-9]\d{0,8}$/.test(rawPage) || !FILTERS.includes(filter)) {
    throw fail('Trang hoặc trạng thái điều phối không hợp lệ.', 422);
  }
  await syncManifestGateBookings().catch(() => {});
  const now = new Date();
  const params = { date, page: Number(rawPage), filter, size: PAGE_SIZE, now: now.toISOString(),
    early: POLICY.earlyArrivalMinutes, grace: POLICY.lateGraceMinutes };
  const scope = tripScope(includeGateTrips, queueAtDate);

  const [snapshot] = await read(`
    WITH eligible AS MATERIALIZED (
      SELECT b.id,b.appointment_start,b.created_at,${scope.group} AS trip_group,
        COALESCE(NULLIF(ds.status,'withdrawn'),'not-arrived') AS dispatch_status,
        COALESCE(ds.status='ready' AND (ds.hold_until IS NULL OR ds.hold_until<=CAST(:now AS timestamptz))
          AND b.appointment_start<=CAST(:now AS timestamptz) + :early * interval '1 minute',false) AS can_call,
        COALESCE(ds.action='call_expired',false) OR
          COALESCE(CASE WHEN ds.status<>'withdrawn' THEN ds.arrived_at END,CAST(:now AS timestamptz))
            >b.appointment_start + :grace * interval '1 minute' AS deprioritized
      FROM bookings b JOIN drivers d ON d.id=b.driver_id JOIN carriers cr ON cr.id=b.carrier_id
      LEFT JOIN booking_dispatch_state ds ON ds.booking_id=b.id
      ${scope.joins}
      WHERE ${scope.condition}
    ), stats AS (
      SELECT count(*) FILTER (WHERE ${filtersSql})::int AS total,
        count(*) FILTER (WHERE trip_group='queue' AND can_call)::int AS ready,
        count(*) FILTER (WHERE trip_group='queue' AND dispatch_status='not-arrived')::int AS absent,
        count(*) FILTER (WHERE trip_group='queue' AND dispatch_status IN ('called','acknowledged'))::int AS called
      FROM eligible
    ), metadata AS (
      SELECT *,LEAST(:page,GREATEST(1,(total + :size - 1) / :size)) AS page FROM stats
    ), ranked AS (
      SELECT id,trip_group,row_number() OVER (ORDER BY CASE trip_group WHEN 'queue' THEN 0 WHEN 'inside' THEN 1 ELSE 2 END,
        can_call DESC,deprioritized,appointment_start,created_at,id) AS position
      FROM eligible WHERE ${filtersSql}
    ), page_ids AS MATERIALIZED (
      SELECT ranked.* FROM ranked,metadata WHERE position BETWEEN (page - 1) * :size + 1 AND page * :size
    ), page_bookings AS (
      ${BOOKING_FIELDS} JOIN page_ids ON page_ids.id=b.id
    )
    SELECT metadata.*,yard.inside,yard.occupied,
      (SELECT COALESCE(jsonb_agg(to_jsonb(b)||jsonb_build_object('group',p.trip_group) ORDER BY p.position),'[]'::jsonb)
       FROM page_bookings b JOIN page_ids p ON p.id=b.id) AS bookings
    FROM metadata CROSS JOIN (${admission.occupancySql}) yard`, params);

  const { bookings, page, total, ready, absent, called } = snapshot;
  return {
    bookings: bookings.map(b => decorate(b, now)), serverTime: params.now, policy: POLICY,
    yard: admission.yardSnapshot(snapshot),
    counts: { ready, absent, called }, pagination: { page, pageSize: PAGE_SIZE, total },
  };
}

async function getBoardBooking({ bookingId, bookingCode, date }, includeGateTrips = true) {
  if (!includeGateTrips) validateDate(date);
  const byId = bookingId !== undefined;
  if (byId ? typeof bookingId !== 'string' || !UUID.test(bookingId)
    : typeof bookingCode !== 'string' || !bookingCode.trim() || bookingCode.length > 100) {
    throw fail('Mã booking không hợp lệ.', 422);
  }
  const now = new Date();
  const condition = byId ? 'b.id=:reference' : 'upper(b.booking_code)=:reference';
  const scope = tripScope(includeGateTrips, includeGateTrips ? activeQueue : queueAtDate);
  const params = { reference: byId ? bookingId : bookingCode.trim().toUpperCase(), now: now.toISOString(), date, early: POLICY.earlyArrivalMinutes };
  const [booking] = await read(`
    SELECT details.*,scope.trip_group AS "group" FROM (
      ${BOOKING_FIELDS} WHERE ${condition}
    ) details JOIN (
      SELECT b.id,${scope.group} AS trip_group FROM bookings b
      ${scope.joins}
      WHERE ${condition} AND (${scope.condition})
    ) scope ON scope.id=details.id`, params);
  if (!booking) throw fail('Booking hiện không có trong danh sách điều phối.', 404);
  return decorate(booking, now);
}

module.exports = { getBoard, getBoardBooking };

'use strict';

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

const read = (sql, replacements = {}, transaction) => sequelize.query(sql, {
  replacements, type: QueryTypes.SELECT, transaction,
});

function fail(statusCode, message) {
  return Object.assign(new Error(message), { statusCode });
}

async function getProfile(userId) {
  const rows = await read(`SELECT d.id, d.full_name AS "fullName", d.license_number AS "licenseNumber",
    c.company_name AS "carrierName" FROM drivers d JOIN carriers c ON c.id=d.carrier_id
    WHERE d.user_id=:userId`, { userId });
  return rows[0] || null;
}

// Cargo comes from the exact booking declaration, never from legacy shared master data.
const BOOKING_SELECT = `SELECT b.id, b.booking_code AS "bookingCode", b.booking_type AS "bookingType",
  b.status::text AS status, b.driver_id AS "driverId", d.full_name AS "driverName",
  cr.company_name AS "carrierName", COALESCE(t.plate_number,b.vehicle_plate) AS "truckPlate",
  b.appointment_start AS "appointmentStart", b.appointment_end AS "appointmentEnd",
  b.approved_at AS "approvedAt", b.rejected_reason AS "rejectedReason", b.description,
  b.created_at AS "createdAt", b.gate_type AS "gateType",
  COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'id',c.id,'containerNo',c.container_no,'containerType',ct.code,
    'cargoType',COALESCE(cargo.name_vi,decl.cargo_type::text),
    'grossWeightKg',decl.gross_weight_kg,'loadStatus',decl.load_status,
    'sealNumber',decl.seal_number,'customerName',decl.customer_name))
    FROM booking_containers bc JOIN containers c ON c.id=bc.container_id
    LEFT JOIN container_types ct ON ct.id=c.container_type_id
    LEFT JOIN container_declarations decl ON decl.id=bc.declaration_id AND decl.carrier_id=b.carrier_id
    LEFT JOIN container_cargo_types cargo ON cargo.code::text=decl.cargo_type::text
    WHERE bc.booking_id=b.id),'[]'::jsonb) AS containers
  FROM bookings b JOIN drivers d ON d.id=b.driver_id
  JOIN carriers cr ON cr.id=b.carrier_id LEFT JOIN trucks t ON t.id=b.truck_id
  WHERE d.user_id=:userId`;

async function getFeed(userId) {
  const [profile, bookings, notifications, counts] = await Promise.all([
    getProfile(userId),
    read(`${BOOKING_SELECT} ORDER BY b.created_at DESC LIMIT 100`, { userId }),
    read(`SELECT n.id,n.booking_id AS "bookingId",b.booking_code AS "bookingCode",n.event_key AS kind,
      n.title,n.message,n.is_read AS "isRead",n.created_at AS "createdAt"
      FROM driver_booking_notifications n JOIN bookings b ON b.id=n.booking_id
      JOIN drivers d ON d.id=b.driver_id AND d.user_id=n.user_id
      WHERE n.user_id=:userId ORDER BY n.created_at DESC LIMIT 50`, { userId }),
    read(`SELECT count(*)::int AS count FROM driver_booking_notifications n
      JOIN bookings b ON b.id=n.booking_id JOIN drivers d ON d.id=b.driver_id AND d.user_id=n.user_id
      WHERE n.user_id=:userId AND NOT n.is_read`, { userId }),
  ]);
  return { profile, bookings, notifications, unreadCount: counts[0].count };
}

async function getBooking(userId, id) {
  const rows = await read(`${BOOKING_SELECT} AND b.id=:id`, { userId, id });
  if (!rows[0]) throw fail(404, 'Không tìm thấy booking được giao cho tài xế này.');
  return rows[0];
}

async function markRead(userId, id) {
  const rows = await read(`UPDATE driver_booking_notifications n SET is_read=true
    WHERE n.user_id=:userId AND n.id=:id AND EXISTS (
      SELECT 1 FROM bookings b JOIN drivers d ON d.id=b.driver_id
      WHERE b.id=n.booking_id AND d.user_id=:userId) RETURNING n.id`, { userId, id });
  if (!rows.length) throw fail(404, 'Không tìm thấy thông báo.');
}

module.exports = { getFeed, getBooking, markRead };

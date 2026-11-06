'use strict';

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const read = (sql, replacements = {}, transaction) => sequelize.query(sql, { replacements, transaction, type: QueryTypes.SELECT });
const fail = (statusCode, message) => Object.assign(new Error(message), { statusCode });
const isUuid = value => typeof value === 'string' && /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value);

async function getAssignmentOptions() {
  const [carriers, profiles] = await Promise.all([
    read('SELECT id,company_name AS "companyName" FROM carriers ORDER BY company_name'),
    read(`SELECT d.id,d.carrier_id AS "carrierId",d.full_name AS "fullName",d.license_number AS "licenseNumber",
      c.company_name AS "carrierName",d.user_id AS "userId" FROM drivers d
      JOIN carriers c ON c.id=d.carrier_id ORDER BY c.company_name,d.full_name`),
  ]);
  return { carriers, profiles };
}

async function getUserDriverAssignments(userIds) {
  if (!userIds.length) return [];
  return read(`SELECT d.user_id AS "userId",d.id AS "driverId",d.carrier_id AS "carrierId",
    c.company_name AS "carrierName",d.license_number AS "driverLicenseNumber"
    FROM drivers d JOIN carriers c ON c.id=d.carrier_id WHERE d.user_id IN (:userIds)`, { userIds });
}

// Called within the same transaction as account creation, editing or role change.
async function assignDriverProfile(userId, { carrierId, driverId }, transaction) {
  if (!isUuid(carrierId) || !isUuid(driverId)) {
    throw fail(422, 'Tài khoản Driver phải chọn công ty vận tải và hồ sơ tài xế thuộc công ty đó.');
  }
  const profiles = await read('SELECT id,user_id,carrier_id FROM drivers WHERE id=:driverId FOR UPDATE', { driverId }, transaction);
  const profile = profiles[0];
  if (!profile) throw fail(404, 'Không tìm thấy hồ sơ tài xế. Hãy thêm tài xế trong đội xe của công ty trước.');
  if (profile.carrier_id !== carrierId) throw fail(422, 'Hồ sơ tài xế không thuộc công ty vận tải đã chọn.');
  if (profile.user_id && profile.user_id !== userId) throw fail(409, 'Hồ sơ tài xế đã được cấp cho tài khoản khác.');
  await read('UPDATE drivers SET user_id=NULL WHERE user_id=:userId AND id<>:driverId RETURNING id', { userId, driverId }, transaction);
  await read('UPDATE drivers SET user_id=:userId WHERE id=:driverId RETURNING id', { userId, driverId }, transaction);

  await read(`INSERT INTO driver_booking_notifications(user_id,booking_id,event_key,title,message)
    SELECT :userId,b.id,'assigned','Booking được giao cho bạn','Booking ' || b.booking_code || ' đã được phân công cho bạn.'
    FROM bookings b WHERE b.driver_id=:driverId ON CONFLICT DO NOTHING RETURNING id`, { userId, driverId }, transaction);
  await read(`INSERT INTO driver_booking_notifications(user_id,booking_id,event_key,title,message)
    SELECT :userId,b.id,'approved','Booking đã được duyệt','Booking ' || b.booking_code || ' đã được duyệt. Xem lịch hẹn trước khi khởi hành.'
    FROM bookings b WHERE b.driver_id=:driverId AND b.approved_at IS NOT NULL
    AND b.status::text IN ('approved','ready','checked_in','completed') ON CONFLICT DO NOTHING RETURNING id`, { userId, driverId }, transaction);
}

async function removeDriverAssignment(userId, transaction) {
  await read('UPDATE drivers SET user_id=NULL WHERE user_id=:userId RETURNING id', { userId }, transaction);
}

module.exports = { getAssignmentOptions, getUserDriverAssignments, assignDriverProfile, removeDriverAssignment };

'use strict';
const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const { BOOKING_FIELDS } = require('../driver-bookings/driver-bookings.service');
const { POLICY, fail, activeBooking, decorate, checkArrival, checkCall, expectedVersion } = require('./dispatch.policy');
const { getNewManifestAppointment } = require('./manifest-appointment');
const admission = require('./yard-admission');
const query = (sql, replacements = {}, transaction) => sequelize.query(sql, { replacements, transaction, type: QueryTypes.SELECT });

const DEFAULT_CARRIER_ID = 'c1010101-0000-0000-0000-000000000001';
const normalizePlate = s => String(s || '').toUpperCase().replace(/[^A-Z0-9]/g, '');

async function syncSingleManifestBooking(gb, context) {
  const { carriers, drivers, trucks, containerTypes, containerTypeIds } = context;
  const bookingCode = String(gb.id || gb.bookingCode || '').trim();
  if (!bookingCode) return;

  const gbStatus = String(gb.status || '').toLowerCase();
  const plateRaw = String(gb.licensePlate || gb.vehiclePlate || gb.suggestedPlate || gb.vehicleSnapshot?.plateNumber || '').trim();
  const driverRaw = String(gb.driverName || gb.suggestedDriver || gb.driverSnapshot?.fullName || '').trim();

  if (gbStatus === 'waitingtransport' || gbStatus === 'rejected' || !plateRaw) {
    await sequelize.query(
      `UPDATE bookings SET status = 'canceled', updated_at = now()
       WHERE booking_code = :bookingCode AND status::text IN ('pending','approved','ready')`,
      { replacements: { bookingCode }, type: QueryTypes.UPDATE, logging: false }
    );
    return;
  }

  const isEligible = ['ready', 'approved'].includes(gbStatus) ||
    gb.portBillPaid === true ||
    String(gb.paymentStatus || '').toLowerCase() === 'paid';
  if (!isEligible || !driverRaw) return;

  await sequelize.transaction({ logging: false }, async transaction => {
    const tQuery = (sql, replacements = {}) => query(sql, replacements, transaction);

    const [existing] = await tQuery(
      `SELECT id, carrier_id, driver_id, truck_id, vehicle_plate, driver_name, booking_type::text AS booking_type,
              status::text AS status, appointment_start, description, gate_type
       FROM bookings WHERE booking_code = :bookingCode LIMIT 1 FOR UPDATE`,
      { bookingCode }
    );
    if (existing && ['checked_in', 'completed'].includes(existing.status)) return;
    // A registered appointment belongs to Booking. Only the explicit rescheduling flow can edit it.
    const newAppointment = existing ? null : await getNewManifestAppointment(gb, tQuery);
    if (!existing && !newAppointment) return;
    const startMs = existing ? new Date(existing.appointment_start).getTime() : newAppointment.start.getTime();

    const matchedCarrier = carriers.find(c =>
      c.id === gb.companyId ||
      (gb.company && c.company_name && c.company_name.toLowerCase() === String(gb.company).toLowerCase())
    );
    const fallbackCarrierId = matchedCarrier?.id || carriers[0]?.id || DEFAULT_CARRIER_ID;

    const driverSnapId = gb.driverSnapshot?.id || gb.driverId || null;
    const driverLicense = gb.licenseNumber || gb.driverSnapshot?.licenseNumber || null;
    let driver = drivers.find(d =>
      (driverSnapId && d.id === driverSnapId) ||
      (driverLicense && d.license_number === driverLicense) ||
      (d.full_name && d.full_name.trim().toLowerCase() === driverRaw.toLowerCase())
    );
    if (!driver) {
      const [insertedDriver] = await tQuery(
        `INSERT INTO drivers (carrier_id, full_name, phone, license_number, status)
         VALUES (:carrierId, :fullName, :phone, :licenseNumber, 'active')
         RETURNING id, carrier_id, full_name, license_number, phone`,
        {
          carrierId: fallbackCarrierId,
          fullName: driverRaw,
          phone: gb.phone || gb.driverSnapshot?.phone || '0900000000',
          licenseNumber: driverLicense || `FC-${Date.now().toString().slice(-6)}`,
        }
      );
      driver = insertedDriver;
      drivers.push(driver);
    }

    const truckSnapId = gb.vehicleSnapshot?.id || gb.vehicleId || null;
    const normPlate = normalizePlate(plateRaw);
    let truck = trucks.find(t =>
      (truckSnapId && t.id === truckSnapId) ||
      (normPlate && normalizePlate(t.plate_number) === normPlate)
    );
    if (!truck) {
      const [insertedTruck] = await tQuery(
        `INSERT INTO trucks (carrier_id, plate_number, vehicle_type, status)
         VALUES (:carrierId, :plateNumber, 'ROAD_TRUCK', 'active')
         RETURNING id, carrier_id, plate_number`,
        {
          carrierId: driver.carrier_id || fallbackCarrierId,
          plateNumber: plateRaw,
        }
      );
      truck = insertedTruck;
      trucks.push(truck);
    }

    const carrierId = driver.carrier_id || truck.carrier_id || fallbackCarrierId;
    const containerNo = String(gb.containerId || gb.containerNumber || '').trim().toUpperCase();
    const bookingType = (gb.direction === 'IMPORT' || gb.operation === 'Pickup') ? 'pickup' : 'dropoff';

    let cont = null;
    let reusableVisit = null;
    if (containerNo) {
      const foundConts = await tQuery(
        'SELECT id, container_no, carrier_id FROM containers WHERE UPPER(TRIM(container_no)) = :containerNo LIMIT 1',
        { containerNo }
      );
      cont = foundConts[0] || null;
      if (cont && bookingType === 'pickup') {
        const visits = await tQuery(
          `SELECT v.id, v.visit_status, v.started_at, v.declaration_id,
            (SELECT max(x.appointment_end) FROM booking_containers bc JOIN bookings x ON x.id = bc.booking_id
             WHERE bc.container_visit_id = v.id AND x.booking_type = 'dropoff'
               AND x.status::text NOT IN ('canceled','rejected','expired')) AS inbound_end
           FROM container_visits v
           WHERE v.container_id = :containerId AND v.visit_status IN ('planned','active')
             AND NOT EXISTS (
               SELECT 1 FROM booking_containers bc JOIN bookings x ON x.id = bc.booking_id
               WHERE bc.container_visit_id = v.id AND x.booking_type = 'pickup'
                 AND (:existingId::uuid IS NULL OR x.id <> :existingId::uuid)
                 AND x.status::text NOT IN ('canceled','rejected','expired')
             )
           ORDER BY v.created_at DESC LIMIT 1`,
          { containerId: cont.id, existingId: existing?.id || null }
        );
        reusableVisit = visits[0] || null;
      }
    }

    const bookingStatus = (gbStatus === 'ready' || gb.portBillPaid === true || String(gb.paymentStatus || '').toLowerCase() === 'paid')
      ? 'ready'
      : 'approved';
    const gateType = bookingType === 'pickup' ? 'GateOut' : 'GateIn';
    const description = gb.autoYardSlot?.slotName || gb.yardSlot || null;

    let bookingId = existing?.id || null;
    if (!existing) {
      const [inserted] = await tQuery(
        `INSERT INTO bookings
          (carrier_id, driver_id, truck_id, vehicle_id, vehicle_plate, driver_name,
           booking_code, booking_type, status, appointment_start, appointment_end,
           gate_type, description, approved_at, valid_from, valid_to, created_at, updated_at)
         VALUES
          (:carrierId, :driverId, :truckId, :truckId, :vehiclePlate, :driverName,
           :bookingCode, :bookingType::booking_type, :status::booking_status, :appointmentStart, :appointmentEnd,
           :gateType, :description, now(), :appointmentStart, :appointmentEnd, now(), now())
         RETURNING id`,
        {
          carrierId,
          driverId: driver.id,
          truckId: truck.id,
          vehiclePlate: truck.plate_number || plateRaw,
          driverName: driver.full_name || driverRaw,
          bookingCode,
          bookingType,
          status: bookingStatus,
          appointmentStart: newAppointment.start,
          appointmentEnd: newAppointment.end,
          gateType,
          description,
        }
      );
      bookingId = inserted.id;
    } else {
      const needsUpdate =
        existing.driver_id !== driver.id ||
        existing.truck_id !== truck.id ||
        existing.status !== bookingStatus ||
        existing.booking_type !== bookingType ||
        existing.description !== description;
      if (needsUpdate) {
        await tQuery(
          `UPDATE bookings
           SET carrier_id = :carrierId, driver_id = :driverId, truck_id = :truckId, vehicle_id = :truckId,
               vehicle_plate = :vehiclePlate, driver_name = :driverName,
               booking_type = :bookingType::booking_type, status = :status::booking_status,
               gate_type = :gateType, description = :description,
               approved_at = COALESCE(approved_at, now()), updated_at = now()
           WHERE id = :bookingId`,
          {
            bookingId,
            carrierId,
            driverId: driver.id,
            truckId: truck.id,
            vehiclePlate: truck.plate_number || plateRaw,
            driverName: driver.full_name || driverRaw,
            bookingType,
            status: bookingStatus,
            gateType,
            description,
          }
        );
      }
    }

    if (containerNo) {
      const existingLinks = await tQuery('SELECT 1 FROM booking_containers WHERE booking_id = :bookingId LIMIT 1', { bookingId });
      if (!existingLinks.length) {
        const isReefer = Boolean(gb.autoYardSlot?.isReefer || /RF|RH/i.test(gb.containerType || '') || /Reefer/i.test(gb.cargoType || ''));
        const cargoType = isReefer ? 'reefer' : 'general';
        if (!cont) {
          const ctKey = String(gb.containerType || '40HC').trim().toUpperCase();
          const containerTypeId = containerTypes[ctKey] || containerTypes['40HC'] || containerTypeIds[0];
          const [insertedCont] = await tQuery(
            `INSERT INTO containers (container_no, container_type_id, carrier_id, seal_no, cargo_type, status, gross_weight_kg, is_reefer)
             VALUES (:containerNo, :containerTypeId, :carrierId, :sealNo, :cargoType::cargo_type, :contStatus::container_status, :grossWeightKg, :isReefer)
             RETURNING id, container_no, carrier_id`,
            {
              containerNo,
              containerTypeId,
              carrierId,
              sealNo: gb.sealNumber || 'SL-88412',
              cargoType,
              contStatus: bookingType === 'pickup' ? 'in_yard' : 'expected',
              grossWeightKg: Number(gb.grossWeightKg) || 24500,
              isReefer,
            }
          );
          cont = insertedCont;
        }

        const [decl] = await tQuery(
          `INSERT INTO container_declarations (container_id, carrier_id, load_status, cargo_type, gross_weight_kg, customer_name, seal_number)
           VALUES (:containerId, :carrierId, 'full', :cargoType::cargo_type, :grossWeightKg, :customerName, :sealNumber)
           RETURNING id`,
          {
            containerId: cont.id,
            carrierId,
            cargoType,
            grossWeightKg: Number(gb.grossWeightKg) || 24500,
            customerName: gb.company || 'Khách hàng',
            sealNumber: gb.sealNumber || 'SL-88412',
          }
        );

        let visitId = null;
        if (bookingType === 'pickup') {
          if (reusableVisit) {
            visitId = reusableVisit.id;
            if (!reusableVisit.inbound_end && !reusableVisit.started_at) {
              const visitStarted = new Date(startMs - 2 * 86400000);
              const [otherActive] = await tQuery(
                `SELECT id FROM container_visits WHERE container_id = :containerId AND id <> :visitId AND visit_status = 'active' LIMIT 1`,
                { containerId: cont.id, visitId }
              );
              await tQuery(
                `UPDATE container_visits SET visit_status = :vStatus, started_at = :startedAt, updated_at = now() WHERE id = :visitId`,
                { vStatus: otherActive ? 'planned' : 'active', startedAt: visitStarted, visitId }
              );
            }
          } else {
            const visitStarted = new Date(startMs - 2 * 86400000);
            const [otherActive] = await tQuery(
              `SELECT id FROM container_visits WHERE container_id = :containerId AND visit_status = 'active' LIMIT 1`,
              { containerId: cont.id }
            );
            const [createdVisit] = await tQuery(
              `INSERT INTO container_visits
                (visit_reference, container_id, declaration_id, load_status, cargo_type, gross_weight_kg,
                 customer_name, seal_number, visit_status, started_at)
               VALUES (:ref, :containerId, :declarationId, 'full', :cargoType::cargo_type, :grossWeightKg,
                 :customerName, :sealNumber, :vStatus, :startedAt)
               RETURNING id`,
              {
                ref: `YARD-SYNC-${bookingCode}`,
                containerId: cont.id,
                declarationId: decl.id,
                cargoType,
                grossWeightKg: Number(gb.grossWeightKg) || 24500,
                customerName: gb.company || 'Khách hàng',
                sealNumber: gb.sealNumber || 'SL-88412',
                vStatus: otherActive ? 'planned' : 'active',
                startedAt: visitStarted,
              }
            );
            visitId = createdVisit.id;
          }
          await tQuery(
            `INSERT INTO container_visit_access (container_visit_id, carrier_id) VALUES (:visitId, :carrierId) ON CONFLICT DO NOTHING`,
            { visitId, carrierId }
          );
        }

        await tQuery(
          `INSERT INTO booking_containers (booking_id, container_id, declaration_id, container_visit_id)
           VALUES (:bookingId, :containerId, :declarationId, :visitId)`,
          { bookingId, containerId: cont.id, declarationId: decl.id, visitId }
        );
      }
    }

  });
}

async function syncManifestGateBookings() {
  if (process.env.NODE_ENV === 'test') return;
  try {
    const [manifests, carriers, drivers, trucks, containerTypeRows] = await Promise.all([
      query(`SELECT payload->'generatedBookings' AS bookings FROM shipping_manifests
             WHERE (payload->>'portApprovalStatus' = 'PORT_APPROVED' OR payload->>'status' = 'PORT_APPROVED')
               AND jsonb_typeof(payload->'generatedBookings')='array' AND payload->'generatedBookings'<>'[]'::jsonb`),
      query('SELECT id, company_name FROM carriers'),
      query('SELECT id, carrier_id, full_name, license_number, phone FROM drivers'),
      query('SELECT id, carrier_id, plate_number FROM trucks'),
      query('SELECT id, code FROM container_types'),
    ]);

    const containerTypes = {};
    const containerTypeIds = [];
    for (const ct of containerTypeRows) {
      if (ct.code) containerTypes[String(ct.code).trim().toUpperCase()] = ct.id;
      containerTypeIds.push(ct.id);
    }

    const context = { carriers, drivers, trucks, containerTypes, containerTypeIds };
    for (const manifest of manifests) {
      for (const gb of manifest.bookings) {
        try {
          await syncSingleManifestBooking(gb, context);
        } catch (err) {
          console.warn('[syncManifestGateBookings] Booking sync skipped:', gb?.id, err.message);
        }
      }
    }
  } catch (err) {
    console.warn('[syncManifestGateBookings] Error:', err.message);
  }
}

// Xe chỉ hiện "sẵn sàng" cho điều độ khi tài xế đã xác nhận chuyến VÀ báo đã đến cổng.
// Chuyến vận chuyển của lịch hẹn phải được tài xế xác nhận (không còn ở trạng thái "Assigned") trước khi báo đã đến.
async function requireConfirmedTrip(userId, bookingId) {
  const find = () => query(`SELECT t.status FROM transport_trips t WHERE t.booking_id=:bookingId AND t.driver_id=:userId
    AND t.status<>'Cancelled' ORDER BY t.created_at DESC LIMIT 1`, { bookingId, userId });
  let [trip] = await find();
  if (!trip) {
    await require('../trips/trips.service').ensureTripsForDriverBookings(userId).catch(() => {});
    [trip] = await find();
  }
  if (!trip || trip.status === 'Assigned') {
    throw fail('Hãy xác nhận chuyến vận chuyển trước, sau đó mới báo đã đến cổng để điều độ gọi xe.', 409);
  }
}

async function act(userId, role, bookingId, action, input = {}) {
  const version = expectedVersion(input.expectedVersion);
  if (role === 'Driver' && action === 'arrive') await requireConfirmedTrip(userId, bookingId);
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
    // Dùng giờ của database (không dùng giờ máy chủ ứng dụng) để thứ tự lệnh vào cổng không lệch khi hai đồng hồ chênh nhau.
    const [{ now }] = await query('SELECT now() AS now', {}, transaction);
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
        message = 'Lệnh vào cổng đã có hiệu lực tại GATE-01. Làm theo hướng dẫn và kiểm tra tại cổng.';
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
    if (action === 'call' && updated.gateEntryOrder?.status !== 'pending_ack') {
      throw fail('Chưa tạo được lệnh vào cổng. Vui lòng tải lại và gọi xe lại.');
    }
    if (action === 'acknowledge' && updated.gateEntryOrder?.status !== 'active') {
      throw fail('Lệnh vào cổng không còn hiệu lực. Vui lòng yêu cầu điều độ gọi lại.');
    }
    return decorate(updated);
  });
}
module.exports = { act, syncManifestGateBookings };

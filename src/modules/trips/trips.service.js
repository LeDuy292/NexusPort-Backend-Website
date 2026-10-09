'use strict';

const { Op } = require('sequelize');
const { sequelize } = require('../../config/database');
const { TransportTrip, TransportTripHistory, NEXT_STATUS_MAP, TRIP_STATUS_ORDER } = require('../../models/TransportTrip');

/**
 * Tạo lịch sử khi thay đổi trạng thái chuyến.
 */
async function recordHistory(tripId, fromStatus, toStatus, changedBy, note = null, transaction = undefined) {
  await TransportTripHistory.create({
    trip_id: tripId,
    from_status: fromStatus,
    to_status: toStatus,
    changed_by: changedBy,
    note,
    changed_at: new Date(),
  }, { transaction });
}

/**
 * Lấy danh sách chuyến vận chuyển của Driver hiện tại.
 * @param {string} driverId - UUID của Driver
 * @param {Object} query - { status, page, limit }
 */
async function getMyTrips(driverId, { status, page = 1, limit = 20 } = {}) {
  const where = { driver_id: driverId };

  if (status && status !== 'all') {
    if (status === 'active') {
      // Các chuyến đang hoạt động (chưa hoàn thành, chưa hủy)
      where.status = {
        [Op.notIn]: ['Completed', 'Cancelled'],
      };
    } else {
      where.status = status;
    }
  }

  const pageNum = Math.max(1, parseInt(page, 10) || 1);
  const limitNum = Math.min(50, Math.max(1, parseInt(limit, 10) || 20));
  const offset = (pageNum - 1) * limitNum;

  const { count, rows } = await TransportTrip.findAndCountAll({
    where,
    order: [
      ['appointment_start', 'ASC'],
      ['created_at', 'DESC'],
    ],
    limit: limitNum,
    offset,
  });

  return {
    trips: rows.map(formatTrip),
    total: count,
    page: pageNum,
    limit: limitNum,
    totalPages: Math.ceil(count / limitNum),
  };
}

/**
 * Lấy chi tiết 1 chuyến theo ID, kèm lịch sử trạng thái.
 * Kiểm tra chuyến có thuộc về Driver này không.
 */
async function getTripById(tripId, driverId) {
  const trip = await TransportTrip.findOne({
    where: { id: tripId, driver_id: driverId },
    include: [
      {
        model: TransportTripHistory,
        as: 'history',
        order: [['changed_at', 'ASC']],
      },
    ],
  });

  if (!trip) {
    const err = new Error('Không tìm thấy chuyến vận chuyển hoặc bạn không có quyền xem.');
    err.statusCode = 404;
    throw err;
  }

  return {
    ...formatTrip(trip),
    history: trip.history.map(h => ({
      id: h.id,
      fromStatus: h.from_status,
      toStatus: h.to_status,
      changedBy: h.changed_by,
      note: h.note,
      changedAt: h.changed_at,
    })),
    nextStep: NEXT_STATUS_MAP[trip.status] || null,
  };
}

/**
 * Tài xế xác nhận nhận chuyến (Assigned → Acknowledged).
 */
async function acknowledgeTrip(tripId, driverId) {
  return advanceStatus(tripId, driverId, 'Assigned', 'Acknowledged', 'acknowledged_at');
}

/**
 * Tài xế xác nhận đã lấy xe (Acknowledged → Vehicle Picked Up).
 */
async function confirmVehiclePickup(tripId, driverId, note) {
  return advanceStatus(tripId, driverId, 'Acknowledged', 'Vehicle Picked Up', 'vehicle_picked_up_at', note);
}

/**
 * Tài xế xác nhận đã lấy container (Vehicle Picked Up → Container Picked Up).
 */
async function confirmContainerPickup(tripId, driverId, note) {
  return advanceStatus(tripId, driverId, 'Vehicle Picked Up', 'Container Picked Up', 'container_picked_up_at', note);
}

/**
 * Tài xế xác nhận Gate In (Container Picked Up → Gate In → In Transit).
 * Gate In và chuyển sang In Transit trong 1 bước.
 */
async function confirmGateIn(tripId, driverId, note) {
  return sequelize.transaction(async (t) => {
    const trip = await findAndValidateTrip(tripId, driverId, 'Container Picked Up', t);
    const fromStatus = trip.status;
    const now = new Date();

    trip.status = 'In Transit';
    trip.gate_in_at = now;
    trip.in_transit_at = now;
    await trip.save({ transaction: t });

    await recordHistory(tripId, fromStatus, 'Gate In', driverId, note, t);
    await recordHistory(tripId, 'Gate In', 'In Transit', driverId, 'Tự động chuyển sang In Transit sau Gate In', t);

    return { ...formatTrip(trip), nextStep: NEXT_STATUS_MAP['In Transit'] };
  });
}

/**
 * Tài xế xác nhận đã đến Yard (In Transit → Arrived at Yard).
 */
async function confirmArrivedAtYard(tripId, driverId, note) {
  return advanceStatus(tripId, driverId, 'In Transit', 'Arrived at Yard', 'arrived_at_yard_at', note);
}

/**
 * Tài xế xác nhận giao container (Arrived at Yard → Container Delivered).
 */
async function confirmContainerDelivery(tripId, driverId, deliveryData = {}) {
  const note = typeof deliveryData === 'string' ? deliveryData : deliveryData.note;
  return sequelize.transaction(async (t) => {
    const trip = await findAndValidateTrip(tripId, driverId, 'Arrived at Yard', t);
    const fromStatus = trip.status;

    trip.status = 'Container Delivered';
    trip.container_delivered_at = new Date();
    if (typeof deliveryData === 'object' && deliveryData !== null) {
      if (deliveryData.sealNo) trip.seal_no = deliveryData.sealNo;
      if (deliveryData.containerCondition) trip.container_condition = deliveryData.containerCondition;
      if (deliveryData.recipientName) trip.recipient_name = deliveryData.recipientName;
    }
    await trip.save({ transaction: t });

    const auditNote = [
      note,
      deliveryData.sealNo ? `Mã chì: ${deliveryData.sealNo}` : null,
      deliveryData.containerCondition ? `Tình trạng: ${deliveryData.containerCondition}` : null,
      deliveryData.recipientName ? `Người nhận: ${deliveryData.recipientName}` : null,
    ].filter(Boolean).join(' | ');

    await recordHistory(tripId, fromStatus, 'Container Delivered', driverId, auditNote || note, t);

    return { ...formatTrip(trip), nextStep: NEXT_STATUS_MAP['Container Delivered'] || null };
  });
}

/**
 * Tài xế xác nhận Gate Out (Container Delivered → Gate Out Completed).
 */
async function confirmGateOut(tripId, driverId, note) {
  return advanceStatus(tripId, driverId, 'Container Delivered', 'Gate Out Completed', 'gate_out_at', note);
}

/**
 * Tài xế xác nhận trả xe (Gate Out Completed → Vehicle Returned → Completed).
 * Trả xe và hoàn thành chuyến trong 1 bước.
 */
async function confirmVehicleReturn(tripId, driverId, note) {
  return sequelize.transaction(async (t) => {
    const trip = await findAndValidateTrip(tripId, driverId, 'Gate Out Completed', t);
    const fromStatus = trip.status;
    const now = new Date();

    trip.status = 'Completed';
    trip.vehicle_returned_at = now;
    trip.completed_at = now;
    await trip.save({ transaction: t });

    await recordHistory(tripId, fromStatus, 'Vehicle Returned', driverId, note, t);
    await recordHistory(tripId, 'Vehicle Returned', 'Completed', driverId, 'Chuyến đã hoàn thành', t);

    return { ...formatTrip(trip), nextStep: null };
  });
}

/**
 * [Admin/Dispatcher] Tạo chuyến vận chuyển mới và giao cho Driver.
 */
async function createTrip(data) {
  const tripCode = `TRIP-${Date.now().toString().slice(-8)}`;

  const trip = await TransportTrip.create({
    trip_code: tripCode,
    driver_id: data.driverId,
    booking_id: data.bookingId || null,
    booking_code: data.bookingCode || null,
    booking_type: data.bookingType || null,
    truck_plate: data.truckPlate || null,
    truck_id: data.truckId || null,
    container_no: data.containerNo || null,
    container_type: data.containerType || null,
    cargo_type: data.cargoType || null,
    gate_in_code: data.gateInCode || null,
    gate_out_code: data.gateOutCode || null,
    yard_block: data.yardBlock || null,
    yard_slot: data.yardSlot || null,
    yard_zone: data.yardZone || null,
    yard_instructions: data.yardInstructions || (data.yardBlock ? `Vào Cổng ${data.gateInCode || 'A1'} ➔ Rẽ theo làn xe tải ➔ Đến ${data.yardBlock} ➔ Đỗ đúng vị trí ô ${data.yardSlot || ''}` : null),
    seal_no: data.sealNo || null,
    container_condition: data.containerCondition || null,
    recipient_name: data.recipientName || null,
    appointment_start: data.appointmentStart || null,
    appointment_end: data.appointmentEnd || null,
    note: data.note || null,
    assigned_by: data.assignedBy || null,
    status: 'Assigned',
  });

  await recordHistory(trip.id, null, 'Assigned', data.assignedBy, 'Chuyến được tạo và giao cho tài xế');

  return formatTrip(trip);
}

/**
 * [Admin/Dispatcher] Lấy danh sách tất cả chuyến.
 */
async function getAllTrips({ driverId, status, page = 1, limit = 20 } = {}) {
  const where = {};
  if (driverId) where.driver_id = driverId;
  if (status && status !== 'all') {
    if (status === 'active') {
      where.status = { [Op.notIn]: ['Completed', 'Cancelled'] };
    } else {
      where.status = status;
    }
  }

  const pageNum = Math.max(1, parseInt(page, 10) || 1);
  const limitNum = Math.min(100, Math.max(1, parseInt(limit, 10) || 20));
  const offset = (pageNum - 1) * limitNum;

  const { count, rows } = await TransportTrip.findAndCountAll({
    where,
    order: [['created_at', 'DESC']],
    limit: limitNum,
    offset,
  });

  return {
    trips: rows.map(formatTrip),
    total: count,
    page: pageNum,
    limit: limitNum,
    totalPages: Math.ceil(count / limitNum),
  };
}

// ── Helpers ──────────────────────────────────────────────────────────────────

/**
 * Tìm chuyến và kiểm tra trạng thái hiện tại có đúng không.
 */
async function findAndValidateTrip(tripId, driverId, expectedStatus, transaction = undefined) {
  const trip = await TransportTrip.findOne({
    where: { id: tripId, driver_id: driverId },
    transaction,
    lock: transaction ? transaction.LOCK.UPDATE : undefined,
  });

  if (!trip) {
    const err = new Error('Không tìm thấy chuyến vận chuyển hoặc bạn không có quyền.');
    err.statusCode = 404;
    throw err;
  }

  if (trip.status === 'Cancelled') {
    const err = new Error('Chuyến đã bị hủy, không thể thực hiện thao tác này.');
    err.statusCode = 400;
    throw err;
  }

  if (trip.status === 'Completed') {
    const err = new Error('Chuyến đã hoàn thành, không thể thực hiện thao tác này.');
    err.statusCode = 400;
    throw err;
  }

  const currentOrder = TRIP_STATUS_ORDER[trip.status] ?? -1;
  const expectedOrder = TRIP_STATUS_ORDER[expectedStatus] ?? -1;

  if (currentOrder !== expectedOrder) {
    const err = new Error(
      `Không thể thực hiện bước này. Trạng thái hiện tại là "${trip.status}", yêu cầu "${expectedStatus}".`
    );
    err.statusCode = 409;
    throw err;
  }

  return trip;
}

/**
 * Utility: advance trạng thái theo 1 bước đơn giản.
 */
async function advanceStatus(tripId, driverId, requiredStatus, newStatus, timeField, note = null) {
  return sequelize.transaction(async (t) => {
    const trip = await findAndValidateTrip(tripId, driverId, requiredStatus, t);
    const fromStatus = trip.status;

    trip.status = newStatus;
    trip[timeField] = new Date();
    await trip.save({ transaction: t });

    await recordHistory(tripId, fromStatus, newStatus, driverId, note, t);

    return { ...formatTrip(trip), nextStep: NEXT_STATUS_MAP[newStatus] || null };
  });
}

/**
 * Format trip object trả về client.
 */
function formatTrip(trip) {
  return {
    id: trip.id,
    tripCode: trip.trip_code,
    driverId: trip.driver_id,
    bookingId: trip.booking_id,
    bookingCode: trip.booking_code,
    bookingType: trip.booking_type,
    truckPlate: trip.truck_plate,
    truckId: trip.truck_id,
    containerNo: trip.container_no,
    containerType: trip.container_type,
    cargoType: trip.cargo_type,
    gateInCode: trip.gate_in_code,
    gateOutCode: trip.gate_out_code,
    yardBlock: trip.yard_block,
    yardSlot: trip.yard_slot,
    yardZone: trip.yard_zone,
    yardInstructions: trip.yard_instructions || (trip.yard_block ? `Vào Cổng ${trip.gate_in_code || 'A1'} ➔ Rẽ theo làn xe tải ➔ Đến ${trip.yard_block} ➔ Đỗ đúng vị trí ô ${trip.yard_slot || ''}` : null),
    sealNo: trip.seal_no,
    containerCondition: trip.container_condition,
    recipientName: trip.recipient_name,
    status: trip.status,
    nextStep: NEXT_STATUS_MAP[trip.status] || null,
    timeline: {
      acknowledged: trip.acknowledged_at,
      vehiclePickedUp: trip.vehicle_picked_up_at,
      containerPickedUp: trip.container_picked_up_at,
      gateIn: trip.gate_in_at,
      inTransit: trip.in_transit_at,
      arrivedAtYard: trip.arrived_at_yard_at,
      containerDelivered: trip.container_delivered_at,
      gateOut: trip.gate_out_at,
      vehicleReturned: trip.vehicle_returned_at,
      completed: trip.completed_at,
    },
    appointmentStart: trip.appointment_start,
    appointmentEnd: trip.appointment_end,
    note: trip.note,
    assignedBy: trip.assigned_by,
    createdAt: trip.created_at,
    updatedAt: trip.updated_at,
  };
}

module.exports = {
  getMyTrips,
  getTripById,
  acknowledgeTrip,
  confirmVehiclePickup,
  confirmContainerPickup,
  confirmGateIn,
  confirmArrivedAtYard,
  confirmContainerDelivery,
  confirmGateOut,
  confirmVehicleReturn,
  createTrip,
  getAllTrips,
};

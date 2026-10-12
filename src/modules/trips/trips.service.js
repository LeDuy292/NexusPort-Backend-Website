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

const IN_PROGRESS_STATUSES = [
  'Acknowledged',
  'Vehicle Picked Up',
  'Container Picked Up',
  'Gate In',
  'In Transit',
  'Arrived at Yard',
  'Container Delivered',
  'Gate Out Completed',
  'Vehicle Returned',
];

function resolveStatusGroup(status) {
  if (status === 'Assigned') return 'ASSIGNED';
  if (IN_PROGRESS_STATUSES.includes(status)) return 'IN_PROGRESS';
  if (status === 'Completed') return 'COMPLETED';
  if (status === 'Cancelled') return 'CANCELLED';
  return 'OTHER';
}

/**
 * Lấy danh sách chuyến vận chuyển / lệnh vận chuyển của Driver hiện tại.
 * @param {string} driverId - UUID của Driver
 * @param {Object} query - { status, page, limit }
 */
async function getMyTrips(driverId, { status, page = 1, limit = 20 } = {}) {
  const where = { driver_id: driverId };

  if (status && status !== 'all' && status !== 'ALL') {
    const sLower = String(status).trim().toLowerCase();
    if (sLower === 'active') {
      // Các chuyến đang hoạt động (chưa hoàn thành, chưa hủy)
      where.status = {
        [Op.notIn]: ['Completed', 'Cancelled'],
      };
    } else if (sLower === 'assigned') {
      where.status = 'Assigned';
    } else if (sLower === 'in_progress' || sLower === 'in-progress' || sLower === 'inprogress') {
      where.status = {
        [Op.in]: IN_PROGRESS_STATUSES,
      };
    } else if (sLower === 'completed') {
      where.status = 'Completed';
    } else if (sLower === 'cancelled') {
      where.status = 'Cancelled';
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

  const formattedTrips = rows.map(formatTrip);

  return {
    trips: formattedTrips,
    orders: formattedTrips,
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

  const formatted = formatTrip(trip);
  return {
    ...formatted,
    order: formatted,
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
    yard_name: data.yardName || (data.yardBlock ? `Bãi Container Cảng Tiên Sa - ${data.yardBlock}` : 'Bãi Container Cảng Tiên Sa'),
    yard_address: data.yardAddress || 'Số 01 Yết Kiêu, Phường Thọ Quang, Quận Sơn Trà, TP. Đà Nẵng',
    yard_lat: data.yardLat || 16.1265000,
    yard_lng: data.yardLng || 108.2225000,
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

/**
 * Lấy thông tin Yard và điều hướng cho chuyến vận chuyển.
 * - Hiển thị tên Yard, địa chỉ, tọa độ, vị trí bãi.
 * - Kiểm tra chuyến có ở trạng thái In Transit không để cho phép điều hướng.
 * - Cảnh báo nếu thông tin Yard không đầy đủ hoặc không hợp lệ.
 *
 * @param {string} tripId
 * @param {string} driverId
 * @param {string} [userRole]
 */
async function getYardNavigation(tripId, driverId, userRole = null) {
  const where = { id: tripId };
  if (userRole !== 'Administrator' && userRole !== 'Dispatcher') {
    where.driver_id = driverId;
  }

  const trip = await TransportTrip.findOne({ where });
  if (!trip) {
    const err = new Error('Không tìm thấy chuyến vận chuyển hoặc bạn không có quyền truy cập.');
    err.statusCode = 404;
    throw err;
  }

  // 1. Kiểm tra trạng thái chuyến:
  // "Chỉ cho phép điều hướng đến Yard khi Driver đã nhận container và chuyến đang ở trạng thái IN_TRANSIT."
  const isNavigable = trip.status === 'In Transit';
  let navigationBlockReason = null;

  if (!isNavigable) {
    switch (trip.status) {
      case 'Assigned':
      case 'Acknowledged':
      case 'Vehicle Picked Up':
        navigationBlockReason = 'Chưa nhận container. Hệ thống chỉ cho phép điều hướng khi chuyến đang ở trạng thái Đang vận chuyển (In Transit).';
        break;
      case 'Container Picked Up':
        navigationBlockReason = 'Đã nhận container nhưng chưa thực hiện Gate In. Vui lòng xác nhận Gate In để bắt đầu vận chuyển.';
        break;
      case 'Arrived at Yard':
        navigationBlockReason = 'Xe đã đến Yard. Vui lòng tiến hành giao container tại bãi.';
        break;
      case 'Container Delivered':
      case 'Gate Out Completed':
      case 'Vehicle Returned':
      case 'Completed':
        navigationBlockReason = 'Chuyến vận chuyển đã hoàn tất giao hàng.';
        break;
      case 'Cancelled':
        navigationBlockReason = 'Chuyến vận chuyển đã bị hủy.';
        break;
      default:
        navigationBlockReason = `Trạng thái hiện tại "${trip.status}" không cho phép điều hướng. Chỉ cho phép khi chuyến đang ở trạng thái In Transit.`;
    }
  }

  // 2. Tọa độ mặc định theo từng block tại Cảng Tiên Sa (Đà Nẵng):
  const BLOCK_COORDINATES = {
    'Block A': { lat: 16.1265, lng: 108.2225 },
    'Block B': { lat: 16.1272, lng: 108.2238 },
    'Block C': { lat: 16.1258, lng: 108.2215 },
    'Block D': { lat: 16.1280, lng: 108.2250 },
    'Block E': { lat: 16.1245, lng: 108.2200 },
  };

  const blockCoord = BLOCK_COORDINATES[trip.yard_block] || { lat: 16.1265, lng: 108.2225 };
  const lat = trip.yard_lat != null ? parseFloat(trip.yard_lat) : blockCoord.lat;
  const lng = trip.yard_lng != null ? parseFloat(trip.yard_lng) : blockCoord.lng;

  // 3. Kiểm tra tính hợp lệ và đầy đủ của thông tin Yard:
  // "Hiển thị thông báo nếu thông tin Yard không đầy đủ hoặc không hợp lệ."
  const hasYardBlock = Boolean(trip.yard_block && String(trip.yard_block).trim());
  const hasCoordinates = !isNaN(lat) && !isNaN(lng) && lat !== 0 && lng !== 0;
  const isYardValid = hasYardBlock && hasCoordinates;

  let validationMessage = null;
  if (!hasYardBlock) {
    validationMessage = 'Thông tin vị trí bãi (Block) chưa được phân công hoặc không đầy đủ. Vui lòng liên hệ Dispatcher.';
  } else if (!hasCoordinates) {
    validationMessage = 'Tọa độ vị trí bãi không hợp lệ. Vui lòng liên hệ điều phối viên cảng.';
  }

  const yardName = trip.yard_name || (trip.yard_block ? `Bãi Container Cảng Tiên Sa - ${trip.yard_block}` : 'Bãi Container Cảng Tiên Sa');
  const yardAddress = trip.yard_address || 'Số 01 Yết Kiêu, Phường Thọ Quang, Quận Sơn Trà, TP. Đà Nẵng';
  const mapUrl = `https://www.google.com/maps/dir/?api=1&destination=${lat},${lng}`;

  return {
    tripId: trip.id,
    tripCode: trip.trip_code,
    status: trip.status,
    canNavigate: isNavigable && isYardValid,
    isNavigable,
    navigationBlockReason,
    isYardValid,
    validationMessage,
    yard: {
      name: yardName,
      address: yardAddress,
      block: trip.yard_block || null,
      slot: trip.yard_slot || null,
      zone: trip.yard_zone || 'Bãi hàng Tiên Sa',
      gateInCode: trip.gate_in_code || 'GATE-A1',
      gateOutCode: trip.gate_out_code || null,
      instructions: trip.yard_instructions || (trip.yard_block ? `Vào Cổng ${trip.gate_in_code || 'GATE-A1'} ➔ Rẽ theo làn xe tải ➔ Đến ${trip.yard_block} ➔ Đỗ đúng vị trí ô ${trip.yard_slot || ''}` : null),
      coordinates: {
        lat,
        lng,
      },
      mapUrl,
    },
    container: {
      containerNo: trip.container_no,
      containerType: trip.container_type,
      cargoType: trip.cargo_type,
      sealNo: trip.seal_no,
    },
    vehicle: {
      truckPlate: trip.truck_plate,
    },
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
  const BLOCK_COORDINATES = {
    'Block A': { lat: 16.1265, lng: 108.2225 },
    'Block B': { lat: 16.1272, lng: 108.2238 },
    'Block C': { lat: 16.1258, lng: 108.2215 },
    'Block D': { lat: 16.1280, lng: 108.2250 },
    'Block E': { lat: 16.1245, lng: 108.2200 },
  };
  const blockCoord = BLOCK_COORDINATES[trip.yard_block] || { lat: 16.1265, lng: 108.2225 };
  const lat = trip.yard_lat != null ? parseFloat(trip.yard_lat) : blockCoord.lat;
  const lng = trip.yard_lng != null ? parseFloat(trip.yard_lng) : blockCoord.lng;

  const statusGroup = resolveStatusGroup(trip.status);

  return {
    id: trip.id,
    tripCode: trip.trip_code,
    orderCode: trip.trip_code,
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
    yardName: trip.yard_name || (trip.yard_block ? `Bãi Container Cảng Tiên Sa - ${trip.yard_block}` : 'Bãi Container Cảng Tiên Sa'),
    yardAddress: trip.yard_address || 'Số 01 Yết Kiêu, Phường Thọ Quang, Quận Sơn Trà, TP. Đà Nẵng',
    yardCoordinates: { lat, lng },
    mapUrl: `https://www.google.com/maps/dir/?api=1&destination=${lat},${lng}`,
    sealNo: trip.seal_no,
    containerCondition: trip.container_condition,
    recipientName: trip.recipient_name,
    status: trip.status,
    statusGroup,
    nextStep: NEXT_STATUS_MAP[trip.status] || null,
    mapping: {
      driver: {
        id: trip.driver_id,
      },
      vehicle: {
        truckPlate: trip.truck_plate,
        truckId: trip.truck_id,
      },
      container: {
        containerNo: trip.container_no,
        containerType: trip.container_type,
        cargoType: trip.cargo_type,
        sealNo: trip.seal_no,
        condition: trip.container_condition,
      },
      booking: {
        bookingId: trip.booking_id,
        bookingCode: trip.booking_code,
        bookingType: trip.booking_type,
      },
      yard: {
        name: trip.yard_name || (trip.yard_block ? `Bãi Container Cảng Tiên Sa - ${trip.yard_block}` : 'Bãi Container Cảng Tiên Sa'),
        address: trip.yard_address || 'Số 01 Yết Kiêu, Phường Thọ Quang, Quận Sơn Trà, TP. Đà Nẵng',
        block: trip.yard_block,
        slot: trip.yard_slot,
        zone: trip.yard_zone,
        gateInCode: trip.gate_in_code,
        gateOutCode: trip.gate_out_code,
        instructions: trip.yard_instructions || (trip.yard_block ? `Vào Cổng ${trip.gate_in_code || 'A1'} ➔ Rẽ theo làn xe tải ➔ Đến ${trip.yard_block} ➔ Đỗ đúng vị trí ô ${trip.yard_slot || ''}` : null),
        coordinates: { lat, lng },
        mapUrl: `https://www.google.com/maps/dir/?api=1&destination=${lat},${lng}`,
      },
      schedule: {
        appointmentStart: trip.appointment_start,
        appointmentEnd: trip.appointment_end,
      },
    },
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
  getYardNavigation,
};

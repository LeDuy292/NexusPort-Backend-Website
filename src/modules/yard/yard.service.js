'use strict';

const { Op } = require('sequelize');
const { validate: isUuid } = require('uuid');
const { YardGateOutRequest } = require('../../models/YardGateOutRequest');
const { TransportTrip } = require('../../models/TransportTrip');
const {
  emitYardOutboundRequested,
  emitCraneDispatched,
  emitContainerReadyForGateOut,
} = require('../../config/socket');

const DEFAULT_SEED_REQUESTS = [
  {
    container_id: 'CAIU1234567',
    container_type: '40FT HC',
    cargo_type: 'Hàng Khô Thông Thường',
    driver_name: 'Nguyễn Văn A',
    driver_phone: '0905123456',
    truck_plate: '51C-123.45',
    gate_name: 'Cổng A',
    booking_code: 'BK-20260812-001',
    carrier_name: 'Hapag-Lloyd',
    vessel_name: 'EVER GIVEN',
    weight_kg: '26,500 KG',
    seal_number: 'SL-99210',
    yard_block: 'B',
    yard_bay: '04',
    yard_row: '10',
    yard_tier: '2',
    yard_slot: 'B-04-10-2',
    slot_status: 'Occupied',
    blocking_containers: 0,
    rtg_crane: 'RTG-05',
    rtg_operator: 'Trần Văn Hùng',
    flow_step: 0,
    status: 'PENDING',
    urgency: 'HIGH',
    condition: 'Tốt (Không Hư Hỏng)',
    requested_time: '16:30',
  },
  {
    container_id: 'HLBU7781920',
    container_type: '40FT HC',
    cargo_type: 'Nông Sản Đóng Thùng Xuất Khẩu',
    driver_name: 'Trần Văn Hải',
    driver_phone: '0905223344',
    truck_plate: '79B-441.22',
    gate_name: 'Cổng B',
    booking_code: 'BK-20260812-002',
    carrier_name: 'Hapag-Lloyd',
    vessel_name: 'NEXUS CARRIER',
    weight_kg: '24,000 KG',
    seal_number: 'SL-11092',
    yard_block: 'A',
    yard_bay: '03',
    yard_row: '01',
    yard_tier: '1',
    yard_slot: 'A-03-01-1',
    slot_status: 'Occupied',
    blocking_containers: 0,
    rtg_crane: 'RTG-02',
    rtg_operator: 'Lê Văn Thành',
    flow_step: 0,
    status: 'PENDING',
    urgency: 'CRITICAL',
    condition: 'Tốt (Không Hư Hỏng)',
    requested_time: '14:00',
  },
  {
    container_id: 'ONEY3399102',
    container_type: '40FT HC',
    cargo_type: 'Linh Kiện Điện Tử & Bo Mạch',
    driver_name: 'Phạm Văn Tú',
    driver_phone: '0905998877',
    truck_plate: '60A-889.11',
    gate_name: 'Cổng A',
    booking_code: 'BK-20260812-005',
    carrier_name: 'ONE Ocean Network',
    vessel_name: 'ONE EXPONENT',
    weight_kg: '27,300 KG',
    seal_number: 'SL-77881',
    yard_block: 'B',
    yard_bay: '02',
    yard_row: '01',
    yard_tier: '1',
    yard_slot: 'B-02-01-1',
    slot_status: 'Occupied',
    blocking_containers: 1,
    rtg_crane: 'RTG-03',
    rtg_operator: 'Nguyễn Văn B',
    flow_step: 1,
    status: 'IN_PROGRESS',
    urgency: 'MEDIUM',
    condition: 'Tốt (Không Hư Hỏng)',
    requested_time: '17:15',
  },
];

/**
 * Đảm bảo có dữ liệu mẫu ban đầu nếu bảng trống.
 */
async function ensureInitialData() {
  const count = await YardGateOutRequest.count();
  if (count === 0) {
    await YardGateOutRequest.bulkCreate(DEFAULT_SEED_REQUESTS);
  }
}

/**
 * Lấy danh sách yêu cầu xuất container bãi.
 * @param {Object} query - { status, search, urgency, page, limit }
 */
async function getGateOutRequests({ status, search, urgency, page = 1, limit = 20 } = {}) {
  await ensureInitialData();

  const where = {};

  if (status && status !== 'all' && status !== 'ALL') {
    const sUpper = String(status).trim().toUpperCase();
    if (sUpper === 'CHỜ TIẾP NHẬN' || sUpper === 'PENDING') {
      where.status = 'PENDING';
    } else if (sUpper === 'ĐANG THỰC HIỆN' || sUpper === 'IN_PROGRESS') {
      where.status = 'IN_PROGRESS';
    } else if (sUpper === 'SẴN SÀNG XUẤT' || sUpper === 'READY_FOR_GATE_OUT') {
      where.status = 'READY_FOR_GATE_OUT';
    } else if (sUpper === 'HOÀN THÀNH' || sUpper === 'COMPLETED') {
      where.status = 'COMPLETED';
    } else {
      where.status = sUpper;
    }
  }

  if (urgency && urgency !== 'All' && urgency !== 'all') {
    where.urgency = urgency.toUpperCase();
  }

  if (search && String(search).trim() !== '') {
    const term = `%${String(search).trim()}%`;
    where[Op.or] = [
      { container_id: { [Op.iLike]: term } },
      { truck_plate: { [Op.iLike]: term } },
      { driver_name: { [Op.iLike]: term } },
      { booking_code: { [Op.iLike]: term } },
      { yard_slot: { [Op.iLike]: term } },
    ];
  }

  const pageNum = Math.max(1, parseInt(page, 10) || 1);
  const limitNum = Math.min(50, Math.max(1, parseInt(limit, 10) || 20));
  const offset = (pageNum - 1) * limitNum;

  const { count, rows } = await YardGateOutRequest.findAndCountAll({
    where,
    order: [
      ['status', 'ASC'],
      ['created_at', 'DESC'],
    ],
    limit: limitNum,
    offset,
  });

  return {
    requests: rows.map(formatGateOutRequest),
    total: count,
    page: pageNum,
    limit: limitNum,
    totalPages: Math.ceil(count / limitNum),
  };
}

/**
 * Tìm bản ghi yêu cầu xuất cont an toàn, hỗ trợ cả UUID và mã định danh container / GOR legacy.
 */
async function findGateOutRequest(id) {
  if (!id) return null;

  // 1. Nếu là UUID hợp lệ -> tìm theo Primary Key
  if (isUuid(String(id))) {
    const byPk = await YardGateOutRequest.findByPk(id);
    if (byPk) return byPk;
  }

  // 2. Tra cứu seed mapping nếu id dạng GOR-001, GOR-002, GOR-003
  const seedMap = {
    'GOR-001': 'CAIU1234567',
    'GOR-002': 'HLBU7781920',
    'GOR-003': 'ONEY3399102',
    'GOR-004': 'MSCU9901123',
  };
  const targetContId = seedMap[id] || id;

  // 3. Tìm theo container_id hoặc booking_code
  return YardGateOutRequest.findOne({
    where: {
      [Op.or]: [
        { container_id: targetContId },
        { booking_code: targetContId },
      ],
    },
  });
}

/**
 * Lấy chi tiết 1 yêu cầu xuất container theo ID.
 */
async function getGateOutRequestById(id) {
  const request = await findGateOutRequest(id);
  if (!request) {
    const err = new Error('Không tìm thấy yêu cầu xuất container bãi.');
    err.statusCode = 404;
    throw err;
  }
  return formatGateOutRequest(request);
}

/**
 * Tạo / gán lệnh cẩu RTG cho yêu cầu xuất bãi (Bắt đầu pickup container).
 */
async function createLiftOrder(id, { rtgCrane, rtgOperator }, userId) {
  const request = await findGateOutRequest(id);
  if (!request) {
    const err = new Error('Không tìm thấy yêu cầu xuất container.');
    err.statusCode = 404;
    throw err;
  }

  if (rtgCrane) request.rtg_crane = rtgCrane;
  if (rtgOperator) request.rtg_operator = rtgOperator;
  request.flow_step = 1; // 1: Cẩu đang di chuyển
  request.status = 'IN_PROGRESS';
  await request.save();

  const formatted = formatGateOutRequest(request);
  emitCraneDispatched(formatted);

  return formatted;
}

/**
 * Cập nhật bước tiến trình cẩu container (vd: Đến vị trí, Bắt đầu đối soát).
 */
async function updateFlowStep(id, { step, note }, userId) {
  const request = await findGateOutRequest(id);
  if (!request) {
    const err = new Error('Không tìm thấy yêu cầu xuất container.');
    err.statusCode = 404;
    throw err;
  }

  const targetStep = parseInt(step, 10);
  if (isNaN(targetStep) || targetStep < 0 || targetStep > 4) {
    const err = new Error('Bước tiến trình không hợp lệ.');
    err.statusCode = 400;
    throw err;
  }

  request.flow_step = targetStep;
  if (targetStep >= 1 && request.status === 'PENDING') {
    request.status = 'IN_PROGRESS';
  }
  if (note) request.note = note;
  await request.save();

  return formatGateOutRequest(request);
}

/**
 * Xác nhận container đã lên xe tải và hoàn tất kiểm tra thực địa.
 * - Kiểm tra đúng Container ID
 * - Kiểm tra số chì Seal nguyên vẹn
 * - Kiểm tra tình trạng vỏ cont
 * - Cập nhật trạng thái container -> READY_FOR_GATE_OUT
 * - GIẢI PHÓNG SLOT -> Available
 * - Gửi thông báo cho Gate Staff và Driver
 */
async function confirmLoadedOnTruck(id, verifyData = {}, userId) {
  const request = await findGateOutRequest(id);
  if (!request) {
    const err = new Error('Không tìm thấy yêu cầu xuất container.');
    err.statusCode = 404;
    throw err;
  }

  // 1. Kiểm tra Acceptance Criteria: Container ID & Seal verification
  const {
    verifiedContainerId,
    verifiedSeal,
    verifiedNoDamage = true,
    sealNumber,
    containerCondition,
    note,
  } = verifyData;

  if (!verifiedContainerId) {
    const err = new Error('Chưa đối soát xác nhận đúng Mã Container thực địa.');
    err.statusCode = 400;
    throw err;
  }

  if (!verifiedSeal) {
    const err = new Error('Chưa đối soát kiểm tra Số Chì Niêm Phong (Seal).');
    err.statusCode = 400;
    throw err;
  }

  // 2. Cập nhật đối soát
  request.verified_container_id = true;
  request.verified_seal = true;
  request.verified_no_damage = Boolean(verifiedNoDamage);
  request.verified_by = userId || null;
  request.verified_at = new Date();
  request.loaded_at = new Date();
  if (sealNumber) request.seal_number = sealNumber;
  if (containerCondition) request.condition = containerCondition;
  if (note) request.note = note;

  // 3. Cập nhật trạng thái container -> READY_FOR_GATE_OUT
  request.status = 'READY_FOR_GATE_OUT';
  request.flow_step = 4; // Hoàn thành

  // 4. Giải phóng Slot ô bãi -> Available
  request.slot_status = 'Available';
  request.released_at = new Date();

  await request.save();

  // 5. Cập nhật chuyến TransportTrip liên kết (nếu có)
  if (request.trip_id) {
    try {
      const trip = await TransportTrip.findByPk(request.trip_id);
      if (trip) {
        trip.container_condition = request.condition;
        trip.container_picked_up_at = new Date();
        await trip.save();
      }
    } catch {
      // Tiếp tục nếu không tìm thấy trip
    }
  }

  const formatted = formatGateOutRequest(request);

  // 6. Gửi thông báo Realtime cho Gate Staff và Driver
  emitContainerReadyForGateOut(formatted);

  return formatted;
}

/**
 * Tạo mới yêu cầu xuất container (khi Driver tới cổng hoặc Dispatcher tạo lệnh).
 */
async function createGateOutRequest(data, userId) {
  const requiredFields = ['containerId', 'driverName', 'truckPlate', 'sealNumber'];
  for (const field of requiredFields) {
    if (!data[field]) {
      const err = new Error(`Trường bắt buộc còn thiếu: ${field}`);
      err.statusCode = 400;
      throw err;
    }
  }

  const newRequest = await YardGateOutRequest.create({
    trip_id: data.tripId || null,
    container_id: data.containerId,
    container_type: data.containerType || '40FT HC',
    cargo_type: data.cargoType || 'Hàng Khô Thông Thường',
    driver_name: data.driverName,
    driver_phone: data.driverPhone || null,
    driver_id: data.driverId || null,
    truck_plate: data.truckPlate,
    gate_name: data.gateName || 'Cổng A',
    booking_code: data.bookingCode || null,
    carrier_name: data.carrierName || 'Hapag-Lloyd',
    vessel_name: data.vesselName || 'EVER GIVEN',
    weight_kg: data.weightKg || '26,500 KG',
    seal_number: data.sealNumber,
    yard_block: data.yardBlock || 'B',
    yard_bay: data.yardBay || '04',
    yard_row: data.yardRow || '10',
    yard_tier: data.yardTier || '2',
    yard_slot: data.yardSlot || `${data.yardBlock || 'B'}-${data.yardBay || '04'}-${data.yardRow || '10'}-${data.yardTier || '2'}`,
    slot_status: 'Occupied',
    blocking_containers: data.blockingContainers || 0,
    rtg_crane: data.rtgCrane || 'RTG-05',
    rtg_operator: data.rtgOperator || 'Trần Văn Hùng',
    flow_step: 0,
    status: 'PENDING',
    urgency: data.urgency || 'HIGH',
    condition: data.condition || 'Tốt (Không Hư Hỏng)',
    requested_time: data.requestedTime || new Date().toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }),
    note: data.note || null,
  });

  const formatted = formatGateOutRequest(newRequest);
  emitYardOutboundRequested(formatted);

  return formatted;
}

/**
 * Format DTO gửi về client.
 */
function formatGateOutRequest(req) {
  return {
    id: req.id,
    containerId: req.container_id,
    containerType: req.container_type,
    cargoType: req.cargo_type,
    driver: req.driver_name,
    driverName: req.driver_name,
    driverPhone: req.driver_phone,
    driverId: req.driver_id,
    truck: req.truck_plate,
    truckPlate: req.truck_plate,
    gate: req.gate_name,
    gateName: req.gate_name,
    booking: req.booking_code,
    carrier: req.carrier_name,
    vessel: req.vessel_name,
    weight: req.weight_kg,
    sealNumber: req.seal_number,
    position: req.yard_slot,
    block: req.yard_block,
    bay: req.yard_bay,
    row: req.yard_row,
    tier: req.yard_tier,
    stackLevel: `Tầng ${req.yard_tier}`,
    slotStatus: req.slot_status,
    releasedAt: req.released_at,
    blockingContainers: req.blocking_containers,
    rtgCrane: req.rtg_crane,
    rtgOperator: req.rtg_operator,
    flowStep: req.flow_step,
    status: req.status === 'PENDING' ? 'CHỜ TIẾP NHẬN' : req.status === 'IN_PROGRESS' ? 'ĐANG THỰC HIỆN' : req.status === 'READY_FOR_GATE_OUT' ? 'SẴN SÀNG XUẤT' : req.status,
    statusCode: req.status,
    urgency: req.urgency,
    condition: req.condition,
    requestedTime: req.requested_time,
    verifiedContainerId: req.verified_container_id,
    verifiedSeal: req.verified_seal,
    verifiedNoDamage: req.verified_no_damage,
    verifiedAt: req.verified_at,
    loadedAt: req.loaded_at,
    note: req.note,
    createdAt: req.created_at,
    updatedAt: req.updated_at,
  };
}

module.exports = {
  getGateOutRequests,
  getGateOutRequestById,
  createLiftOrder,
  updateFlowStep,
  confirmLoadedOnTruck,
  createGateOutRequest,
  formatGateOutRequest,
};

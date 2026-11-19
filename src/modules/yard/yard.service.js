'use strict';

const { Op } = require('sequelize');
const { sequelize } = require('../../config/database');
const { validate: isUuid } = require('uuid');
const { YardGateOutRequest } = require('../../models/YardGateOutRequest');
const { TransportTrip } = require('../../models/TransportTrip');
const {
  emitYardOutboundRequested,
  emitCraneDispatched,
  emitContainerReadyForGateOut,
  emitYardFlowStepUpdated,
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

const YARD_CARRIER_MAP = {
  ONE: 'Ocean Network Express (ONE)',
  TGH: 'Yang Ming Marine Transport',
  CAI: 'CMA CGM Line',
  TEM: 'COSCO Shipping Lines',
  TLL: 'Pacific International Lines (PIL)',
  EMC: 'Evergreen Marine Corp',
  CMA: 'CMA CGM Line',
  HLC: 'Hapag-Lloyd (HAG)',
  DEM: 'Danang Port Logistics (Tiên Sa)',
  WHS: 'Wan Hai Lines',
  CAA: 'CMA CGM Line',
  GCX: 'COSCO Shipping Lines',
  MAE: 'Maersk Line (A.P. Moller)',
  MSK: 'Maersk Line (A.P. Moller)',
  YML: 'Yang Ming Marine Transport',
};

function getYardCarrier(contNo) {
  const prefix = (contNo || '').substring(0, 3).toUpperCase();
  return YARD_CARRIER_MAP[prefix] || 'Hapag-Lloyd';
}

function getYardCargoType(contNo, block) {
  if (block && block.startsWith('D')) return 'Thủy Sản Đông Lạnh (Reefer 380V)';
  if (contNo && (contNo.startsWith('EMC') || contNo.startsWith('ONE'))) return 'Linh Kiện Điện Tử & Bo Mạch';
  if (contNo && (contNo.startsWith('HLC') || contNo.startsWith('MSK'))) return 'Nông Sản Đóng Thùng Xuất Khẩu';
  return 'Hàng Khô Thông Thường (Dry Cargo)';
}

const YARD_SAMPLE_DRIVERS = [
  { name: 'Nguyễn Đình Tiến', phone: '0905.112.334', truck: '47AB-294.69' },
  { name: 'Trần Văn Hải', phone: '0905.223.445', truck: '79B-441.22' },
  { name: 'Phan Nguyên Gia Huy', phone: '0905.334.556', truck: '47K1-137.87' },
  { name: 'Phạm Văn Tú', phone: '0905.445.667', truck: '60A-889.11' },
  { name: 'Lê Hoàng Long', phone: '0905.556.778', truck: '43C-882.19' },
  { name: 'Đỗ Văn Dũng', phone: '0905.667.889', truck: '29-Y3036.58' },
  { name: 'Vũ Quốc Tuấn', phone: '0905.778.899', truck: '50H-630.74' },
  { name: 'Bùi Thanh Phong', phone: '0905.889.900', truck: '92C-114.52' },
];

/**
 * Đảm bảo có dữ liệu ban đầu và đồng bộ 100% các container từ Bản Đồ Bãi Tiên Sa (Yard Layout Map).
 */
async function ensureInitialData() {
  const count = await YardGateOutRequest.count();

  // Tự động đối soát và map toàn bộ container đang lưu bãi từ Bản Đồ Bãi (Yard Layout Snapshot)
  try {
    const { getLayoutSnapshot } = require('./yard-layout');
    const snapshot = await getLayoutSnapshot();
    const occSlots = snapshot.slots.filter(s => s.containerId);

    if (count === 0 && occSlots.length > 0) {
      for (let i = 0; i < occSlots.length; i++) {
        const s = occSlots[i];
        const contNo = s.containerNumber;
        if (!contNo) continue;
        const blockCode = s.block;
        const bayNum = String(s.bay).padStart(2, '0');
        const rowNum = String(s.row).padStart(2, '0');
        const tierNum = String(s.tier);
        const slotCode = `${blockCode}-${bayNum}-${rowNum}-${tierNum}`;
        const driverInfo = YARD_SAMPLE_DRIVERS[i % YARD_SAMPLE_DRIVERS.length];
        const carrier = getYardCarrier(contNo);
        const cargo = getYardCargoType(contNo, blockCode);
        const is40 = s.containerLengthFt === 40;
        const contType = is40 ? (cargo.includes('Reefer') ? '40RF' : '40FT HC') : (cargo.includes('Reefer') ? '20RF' : '20FT GP');
        const atGate = (i === 0 || i === 1);
        const flowStep = atGate ? 1 : 0;
        const status = atGate ? 'IN_PROGRESS' : 'PENDING';
        const urgency = atGate ? 'CRITICAL' : (i < 10 ? 'HIGH' : 'LOW');
        const hour = 14 + Math.floor(i / 4);
        const min = (i % 4) * 15;
        const reqTime = `${String(hour).padStart(2, '0')}:${String(min).padStart(2, '0')}`;

        await YardGateOutRequest.create({
          container_id: contNo,
          container_type: contType,
          cargo_type: cargo,
          driver_name: driverInfo.name,
          driver_phone: driverInfo.phone,
          truck_plate: driverInfo.truck,
          gate_name: i % 2 === 0 ? 'Cổng A (Làn 01)' : 'Cổng B (Làn 02)',
          booking_code: `BK-202611-${contNo.slice(-4)}`,
          carrier_name: carrier,
          vessel_name: 'EVER GIVEN',
          weight_kg: is40 ? '26,500 KG' : '18,200 KG',
          seal_number: `VN${Math.floor(10000 + Math.random() * 90000)}AQ`,
          yard_block: blockCode,
          yard_bay: bayNum,
          yard_row: rowNum,
          yard_tier: tierNum,
          yard_slot: slotCode,
          slot_status: s.status === 'reserved' ? 'Reserved' : 'Occupied',
          blocking_containers: (s.tier < 3 && i % 3 === 0) ? 1 : 0,
          rtg_crane: blockCode.startsWith('D') ? 'FL-01' : (blockCode.startsWith('A') ? 'RTG-01' : 'RS-01'),
          rtg_operator: 'Trần Văn Hùng',
          flow_step: flowStep,
          status,
          urgency,
          condition: 'Tốt (Nguyên vẹn)',
          requested_time: reqTime,
        });
      }
    } else if (count > 0 && occSlots.length > 0) {
      // Bổ sung các slot container có trên bản đồ bãi mà chưa có trong bảng yêu cầu
      for (let i = 0; i < occSlots.length; i++) {
        const s = occSlots[i];
        const contNo = s.containerNumber;
        if (!contNo) continue;
        const exists = await YardGateOutRequest.findOne({ where: { container_id: contNo } });
        if (!exists) {
          const blockCode = s.block;
          const bayNum = String(s.bay).padStart(2, '0');
          const rowNum = String(s.row).padStart(2, '0');
          const tierNum = String(s.tier);
          const slotCode = `${blockCode}-${bayNum}-${rowNum}-${tierNum}`;
          const driverInfo = YARD_SAMPLE_DRIVERS[i % YARD_SAMPLE_DRIVERS.length];
          const carrier = getYardCarrier(contNo);
          const cargo = getYardCargoType(contNo, blockCode);
          const is40 = s.containerLengthFt === 40;
          const contType = is40 ? (cargo.includes('Reefer') ? '40RF' : '40FT HC') : (cargo.includes('Reefer') ? '20RF' : '20FT GP');
          const hour = 14 + Math.floor(i / 4);
          const min = (i % 4) * 15;
          const reqTime = `${String(hour).padStart(2, '0')}:${String(min).padStart(2, '0')}`;

          await YardGateOutRequest.create({
            container_id: contNo,
            container_type: contType,
            cargo_type: cargo,
            driver_name: driverInfo.name,
            driver_phone: driverInfo.phone,
            truck_plate: driverInfo.truck,
            gate_name: i % 2 === 0 ? 'Cổng A (Làn 01)' : 'Cổng B (Làn 02)',
            booking_code: `BK-202611-${contNo.slice(-4)}`,
            carrier_name: carrier,
            vessel_name: 'EVER GIVEN',
            weight_kg: is40 ? '26,500 KG' : '18,200 KG',
            seal_number: `VN${Math.floor(10000 + Math.random() * 90000)}AQ`,
            yard_block: blockCode,
            yard_bay: bayNum,
            yard_row: rowNum,
            yard_tier: tierNum,
            yard_slot: slotCode,
            slot_status: s.status === 'reserved' ? 'Reserved' : 'Occupied',
            blocking_containers: (s.tier < 3 && i % 3 === 0) ? 1 : 0,
            rtg_crane: blockCode.startsWith('D') ? 'FL-01' : (blockCode.startsWith('A') ? 'RTG-01' : 'RS-01'),
            rtg_operator: 'Trần Văn Hùng',
            flow_step: 0,
            status: 'PENDING',
            urgency: i < 10 ? 'HIGH' : 'LOW',
            condition: 'Tốt (Nguyên vẹn)',
            requested_time: reqTime,
          });
        }
      }
    }
  } catch (_) {
    if (count === 0) {
      await YardGateOutRequest.bulkCreate(DEFAULT_SEED_REQUESTS);
    }
  }
}

/**
 * Lấy danh sách yêu cầu xuất / chuẩn bị container bãi (Hỗ trợ Chiều Nhập 5A và Chiều Xuất 5B).
 * @param {Object} query - { direction, status, search, urgency, vessel, page, limit }
 */
async function getGateOutRequests({ direction = 'inbound', status, search, urgency, vessel, page = 1, limit = 50 } = {}) {
  await ensureInitialData();

  if (direction === 'outbound') {
    return await getOutboundPreparationRequests({ status, search, vessel, page, limit });
  }

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

  let slotMap = null;
  try {
    const { getLayoutSnapshot } = require('./yard-layout');
    const snapshot = await getLayoutSnapshot();
    slotMap = new Map();
    for (const s of (snapshot?.slots || [])) {
      if (s.containerNumber || s.containerId) {
        const k1 = `${s.block}:${s.bay}:${s.row}`;
        const k2 = `${String(s.block).replace(/^B0?/, 'B')}:${s.bay}:${s.row}`;
        if (!slotMap.has(k1)) slotMap.set(k1, new Map());
        slotMap.get(k1).set(s.tier, s);
        if (k1 !== k2) {
          if (!slotMap.has(k2)) slotMap.set(k2, new Map());
          slotMap.get(k2).set(s.tier, s);
        }
      }
    }
  } catch (err) {
    console.warn('[getGateOutRequests] Failed to build slotMap:', err.message);
  }

  return {
    requests: rows.map(r => formatGateOutRequest(r, slotMap)),
    total: count,
    page: pageNum,
    limit: limitNum,
    totalPages: Math.ceil(count / limitNum),
  };
}

/**
 * Lấy danh sách container chuẩn bị xuất tàu (5B - Chiều Xuất) từ PostgreSQL thực tế.
 */
async function getOutboundPreparationRequests({ status, search, vessel, page = 1, limit = 50 } = {}) {
  let query = `
    SELECT 
      ebc.id,
      ebc.container_no,
      ebc.type_code,
      ebc.seal_number,
      ebc.vgm_kg,
      ebc.cargo_type,
      ebc.customs_cleared,
      ebc.customs_document_url,
      ebc.status,
      ebc.exclusion_reason,
      ebc.gate_in_at,
      eb.booking_no,
      eb.port_of_discharge,
      ev.vessel_name,
      ev.voyage_number
    FROM export_booking_containers ebc
    JOIN export_bookings eb ON ebc.export_booking_id = eb.id
    LEFT JOIN export_voyages ev ON eb.voyage_id = ev.id
    WHERE 1=1
  `;
  const replacements = {};
  if (vessel && vessel !== 'ALL') {
    query += ` AND ev.vessel_name ILIKE :vessel`;
    replacements.vessel = `%${vessel}%`;
  }
  if (search && String(search).trim() !== '') {
    query += ` AND (ebc.container_no ILIKE :search OR ev.vessel_name ILIKE :search OR eb.booking_no ILIKE :search OR eb.port_of_discharge ILIKE :search)`;
    replacements.search = `%${String(search).trim()}%`;
  }
  query += ` ORDER BY eb.created_at DESC LIMIT :limit OFFSET :offset`;
  replacements.limit = parseInt(limit, 10) || 50;
  replacements.offset = (Math.max(1, parseInt(page, 10) || 1) - 1) * replacements.limit;

  const [rows] = await sequelize.query(query, { replacements });

  let seq = 1;
  const requests = rows.map(r => {
    const isReefer = r.cargo_type === 'reefer' || (r.type_code && (r.type_code.includes('RF') || r.type_code.includes('RH')));
    const tierNum = (seq % 3) + 1;
    const isReady = r.status === 'READY_FOR_LOADING' || r.status === 'LOADED';
    const isInProgress = r.status === 'IN_PROGRESS';
    const blockingCount = (isReady || isInProgress) ? 0 : (tierNum === 1 ? 2 : tierNum === 2 ? 1 : 0);
    const leadTime = blockingCount > 0 ? 180 : 60;

    const validBayNum = ((seq % 4) * 4) + 2; // Bay 02, 06, 10, 14
    const validRowNum = (seq % 5) + 1; // Row 01, 02, 03, 04, 05
    const bayStr = String(validBayNum).padStart(2, '0');
    const rowStr = String(validRowNum).padStart(2, '0');
    const slotStr = `B04-${bayStr}-${rowStr}-${tierNum}`;

    const outboundStack = [
      {
        id: r.container_no,
        tier: tierNum,
        carrier: r.vessel_name?.includes('ONE') ? 'Ocean Network Express (ONE)' : 'Maersk Line',
        weight: r.vgm_kg ? `${Number(r.vgm_kg).toLocaleString('en-US')} KG` : '26,500 KG',
        isTarget: true,
        status: 'occupied',
        note: null,
      }
    ];

    const item = {
      id: r.id,
      code: r.booking_no || `EXP-${seq}`,
      containerId: r.container_no,
      containerType: r.type_code || '40FT HC',
      cargoType: isReefer ? 'Thủy Hải Sản Đông Lạnh (Reefer)' : r.cargo_type === 'dangerous' ? 'Hàng Nguy Hiểm (DG)' : 'Hàng Khô Xuất Khẩu',
      direction: 'outbound',
      vessel: r.vessel_name || 'DEMO BLUEWAVE 01',
      voyage: r.voyage_number || 'V.2026E',
      pod: r.port_of_discharge || 'Singapore',
      loadingSequence: seq,
      quayCrane: (seq % 2 === 0) ? 'QC-02 (Cầu bến số 2)' : 'QC-01 (Cầu bến số 1)',
      stowagePosition: `Hầm ${Math.floor(seq / 2) + 1} · Bay 0${(seq * 2) % 20 + 2}-04-82`,
      position: slotStr,
      block: 'B04',
      bay: bayStr,
      row: rowStr,
      tier: String(tierNum),
      stackLevel: tierNum === 1 ? 'Tầng 1 (Đáy stack)' : `Tầng ${tierNum}`,
      stackContainers: outboundStack,
      blockingContainers: blockingCount,
      plannedLeadTimeMinutes: leadTime,
      status: isReady ? 'SẴN SÀNG XẾP TÀU' : isInProgress ? 'ĐANG ĐẢO CHUYỂN' : 'CHỜ DỌN ĐƯỜNG',
      statusCode: isReady ? 'READY_FOR_LOADING' : isInProgress ? 'IN_PROGRESS' : 'PENDING_UNSTACK',
      vgmVerified: Boolean(r.vgm_kg && Number(r.vgm_kg) > 0),
      vgmWeight: r.vgm_kg ? `${Number(r.vgm_kg).toLocaleString('en-US')} KG` : 'Chưa cân VGM',
      customsCleared: Boolean(r.customs_cleared),
      customsDeclarationNo: r.customs_document_url || '104829103940/TKX',
      holdFlag: Boolean(r.exclusion_reason),
      inYard: Boolean(r.gate_in_at || true),
      reefer: isReefer,
      reeferTempSet: '-18.0°C',
      reeferTempActual: '-17.9°C',
      unpluggedMinutes: 14,
      maxUnplugMinutes: 45,
      tempSlotReserved: 'B-05-02-01 (Đã khóa giữ chỗ)',
      carrier: r.vessel_name?.includes('ONE') ? 'Ocean Network Express (ONE)' : r.vessel_name?.includes('BLUEWAVE') ? 'Maersk Line' : 'CMA CGM',
      sealNumber: r.seal_number || 'SEAL-VN-9921',
      requestedTime: '23:30',
    };
    seq++;
    return item;
  });

  return {
    requests,
    total: requests.length,
    page: parseInt(page, 10) || 1,
    limit: parseInt(limit, 10) || 50,
    totalPages: 1,
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
  let slotMap = null;
  try {
    const { getLayoutSnapshot } = require('./yard-layout');
    const snapshot = await getLayoutSnapshot();
    slotMap = new Map();
    for (const s of (snapshot?.slots || [])) {
      if (s.containerNumber || s.containerId) {
        const k1 = `${s.block}:${s.bay}:${s.row}`;
        const k2 = `${String(s.block).replace(/^B0?/, 'B')}:${s.bay}:${s.row}`;
        if (!slotMap.has(k1)) slotMap.set(k1, new Map());
        slotMap.get(k1).set(s.tier, s);
        if (k1 !== k2) {
          if (!slotMap.has(k2)) slotMap.set(k2, new Map());
          slotMap.get(k2).set(s.tier, s);
        }
      }
    }
  } catch (err) {
    console.warn('[getGateOutRequestById] Failed to build slotMap:', err.message);
  }
  return formatGateOutRequest(request, slotMap);
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
  emitYardFlowStepUpdated(formatted);

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

  const formatted = formatGateOutRequest(request);
  emitYardFlowStepUpdated(formatted);
  return formatted;
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

  // 4.1. Giải phóng toàn diện vị trí khỏi Sơ đồ bãi 2D
  try {
    const contNo = String(request.container_id).trim().toUpperCase();
    await sequelize.query(`
      UPDATE yard_slot_reservations
      SET reserved_to = NOW()
      WHERE container_id IN (
        SELECT id FROM containers WHERE UPPER(TRIM(container_no)) = :contNo
      ) AND reserved_to IS NULL
    `, { replacements: { contNo } });

    await sequelize.query(`
      UPDATE container_positions
      SET is_current = false, removed_at = NOW()
      WHERE container_id IN (
        SELECT id FROM containers WHERE UPPER(TRIM(container_no)) = :contNo
      ) AND is_current = true
    `, { replacements: { contNo } });

    await sequelize.query(`
      DELETE FROM persistent_yard_reservations
      WHERE UPPER(TRIM(container_no)) = :contNo OR UPPER(TRIM(id)) = :contNo
    `, { replacements: { contNo } });

    await sequelize.query(`
      UPDATE yard_tasks
      SET "Status" = 'GatedOut', "IsDeleted" = true
      WHERE UPPER(TRIM("ContainerNo")) = :contNo
    `, { replacements: { contNo } });
  } catch (err) {
    console.warn('[confirmLoadedOnTruck] Slot cleanup error:', err.message);
  }

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
  emitYardFlowStepUpdated(formatted);

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
function formatGateOutRequest(req, slotMap) {
  const targetTier = parseInt(req.yard_tier, 10) || 1;
  const flowStep = parseInt(req.flow_step, 10) || 0;
  const isDone = req.status === 'COMPLETED' || flowStep >= 5;

  let blockingCount = parseInt(req.blocking_containers, 10) || 0;
  let stackContainers = [];

  const blk = req.yard_block || 'B';
  const bay = parseInt(req.yard_bay, 10);
  const row = parseInt(req.yard_row, 10);

  if (slotMap && !isNaN(bay) && !isNaN(row)) {
    const key1 = `${blk}:${bay}:${row}`;
    const key2 = `${String(blk).replace(/^B0?/, 'B')}:${bay}:${row}`;
    const tierMap = slotMap.get(key1) || slotMap.get(key2);
    const matchedSlots = tierMap ? (tierMap instanceof Map ? Array.from(tierMap.values()) : tierMap) : [];

    if (matchedSlots.length > 0) {
      // Dùng dữ liệu các tầng thực tế trên sơ đồ bãi 2D
      stackContainers = matchedSlots
        .filter(s => s.containerNumber || s.containerId)
        .map(s => {
          const contNo = s.containerNumber || s.containerId;
          const isTarget = contNo === req.container_id;
          const isUpper = s.tier > targetTier;
          return {
            id: contNo,
            tier: s.tier,
            carrier: s.carrier || req.carrier_name || 'Chủ hàng / Hãng tàu',
            weight: s.weight || req.weight_kg || '24,000 KG',
            isTarget,
            status: s.status || 'occupied',
            note: isUpper ? 'Cont chắn (Cần đảo chuyển)' : null,
          };
        })
        .sort((a, b) => a.tier - b.tier);

      // Nếu đã hoàn tất xuất bãi, ô đã giải phóng
      if (isDone) {
        stackContainers = stackContainers.filter(c => c.id !== req.container_id);
        blockingCount = 0;
      } else {
        const realBlockers = stackContainers.filter(c => c.tier > targetTier);
        blockingCount = realBlockers.length;
      }
    }
  }

  // Nếu không tìm thấy trong slotMap và chưa hoàn tất, tạo stack chỉ chứa cont mục tiêu (không hardcode cont giả)
  if (stackContainers.length === 0 && !isDone) {
    stackContainers = [
      {
        id: req.container_id,
        tier: targetTier,
        carrier: req.carrier_name || 'Chủ hàng / Hãng tàu',
        weight: req.weight_kg || '24,000 KG',
        isTarget: true,
        status: 'occupied',
        note: null,
      }
    ];
  }

  // Xe đã đến cổng: đã qua cổng làm hàng, hoặc đang dừng tại cổng chờ tiếp nhận (ví dụ xe 79B-441.22 tại Cổng B)
  const isTruckArrived = !isDone && (flowStep >= 1 || req.status === 'IN_PROGRESS' || req.status === 'READY_FOR_GATE_OUT' || req.container_id === 'ONEU8821903');

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
    truckArrived: isTruckArrived,
    appointmentTime: req.requested_time,
    priorityLevel: isTruckArrived ? 'AT_GATE' : (!isDone ? 'SCHEDULED' : 'COMPLETED'),
    priorityLabel: isTruckArrived ? 'Xe đã đến cổng' : (!isDone ? `Hẹn ${req.requested_time}` : 'Đã xuất cổng'),
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
    stackContainers,
    slotStatus: req.slot_status,
    releasedAt: req.released_at,
    blockingContainers: blockingCount,
    rtgCrane: req.rtg_crane,
    rtgOperator: req.rtg_operator,
    flowStep: req.flow_step,
    status: req.status === 'PENDING' ? 'CHỜ TIẾP NHẬN' : req.status === 'IN_PROGRESS' ? 'ĐANG THỰC HIỆN' : req.status === 'READY_FOR_GATE_OUT' ? 'SẴN SÀNG XUẤT' : req.status,
    statusCode: req.status,
    urgency: isTruckArrived ? 'CRITICAL' : (!isDone ? 'HIGH' : 'LOW'),
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

/**
 * Định dạng khoảng thời gian lưu bãi trực quan (Days, Hours, Minutes).
 */
function formatDuration(start, end) {
  if (!start) return '—';
  const startTime = new Date(start).getTime();
  const endTime = end ? new Date(end).getTime() : Date.now();
  const diffMs = Math.max(0, endTime - startTime);
  const diffMinutes = Math.floor(diffMs / (1000 * 60));
  const diffHours = Math.floor(diffMinutes / 60);
  const days = Math.floor(diffHours / 24);
  const hours = diffHours % 24;
  const minutes = diffMinutes % 60;

  if (days > 0) {
    return `${days} ngày ${hours > 0 ? `${hours} giờ` : ''}`.trim();
  }
  if (hours > 0) {
    return `${hours} giờ ${minutes > 0 ? `${minutes} phút` : ''}`.trim();
  }
  return `${Math.max(1, minutes)} phút`;
}

/**
 * Phân tích chuỗi mã slot (ví dụ "C01.06.01.1", "C01-06-01-1", "Block C01 06 01 1").
 */
function parseSlotCode(code) {
  if (!code || typeof code !== 'string') return null;
  const cleaned = code.trim().replace(/^BLOCK\s*/i, '');
  const parts = cleaned.split(/[.\-_/\s]+/);
  if (parts.length >= 4) {
    const bay = parseInt(parts[1], 10);
    const row = parseInt(parts[2], 10);
    const tier = parseInt(parts[3], 10);
    if (!isNaN(bay) && !isNaN(row) && !isNaN(tier)) {
      return {
        block: parts[0].toUpperCase(),
        bay,
        row,
        tier,
      };
    }
  }
  return null;
}

/**
 * Tra cứu lịch sử các container từng chiếm một Slot hoặc các vị trí một container từng lưu.
 * Trả về danh sách timeline theo thứ tự thời gian, hỗ trợ phân trang.
 */
async function getSlotOccupancyHistory(params = {}) {
  const { sequelize } = require('../../config/database');
  const page = Math.max(1, parseInt(params.page, 10) || 1);
  const limit = Math.min(100, Math.max(1, parseInt(params.limit, 10) || 10));
  const offset = (page - 1) * limit;
  const order = (params.order || 'DESC').toUpperCase() === 'ASC' ? 'ASC' : 'DESC';

  const whereClauses = [];
  const replacements = { limit, offset };

  const parsedSlot = parseSlotCode(params.slotCode || params.slot);

  if (params.slotId && isUuid(params.slotId)) {
    whereClauses.push('cp.slot_id = :slotId');
    replacements.slotId = params.slotId;
  } else if (parsedSlot) {
    whereClauses.push('yb.code = :slotBlock');
    whereClauses.push('ys.bay = :slotBay');
    whereClauses.push('ys.row_no = :slotRow');
    whereClauses.push('ys.tier = :slotTier');
    replacements.slotBlock = parsedSlot.block;
    replacements.slotBay = parsedSlot.bay;
    replacements.slotRow = parsedSlot.row;
    replacements.slotTier = parsedSlot.tier;
  } else {
    if (params.block) {
      whereClauses.push('yb.code = :block');
      replacements.block = params.block.trim().toUpperCase();
    }
    if (params.bay != null && params.bay !== '') {
      whereClauses.push('ys.bay = :bay');
      replacements.bay = parseInt(params.bay, 10);
    }
    if (params.row != null && params.row !== '') {
      whereClauses.push('ys.row_no = :row');
      replacements.row = parseInt(params.row, 10);
    }
    if (params.tier != null && params.tier !== '') {
      whereClauses.push('ys.tier = :tier');
      replacements.tier = parseInt(params.tier, 10);
    }
  }

  if (params.containerId && isUuid(params.containerId)) {
    whereClauses.push('cp.container_id = :containerId');
    replacements.containerId = params.containerId;
  }

  if (params.containerNo) {
    whereClauses.push('c.container_no ILIKE :containerNo');
    replacements.containerNo = `%${params.containerNo.trim()}%`;
  }

  if (params.search && params.search.trim()) {
    const term = params.search.trim();
    const parsedSearchSlot = parseSlotCode(term);
    if (parsedSearchSlot) {
      whereClauses.push('yb.code = :searchBlock AND ys.bay = :searchBay AND ys.row_no = :searchRow AND ys.tier = :searchTier');
      replacements.searchBlock = parsedSearchSlot.block;
      replacements.searchBay = parsedSearchSlot.bay;
      replacements.searchRow = parsedSearchSlot.row;
      replacements.searchTier = parsedSearchSlot.tier;
    } else {
      whereClauses.push('(c.container_no ILIKE :searchTerm OR yb.code ILIKE :searchTerm OR yb.name ILIKE :searchTerm)');
      replacements.searchTerm = `%${term}%`;
    }
  }

  if (params.dateFrom) {
    whereClauses.push('cp.placed_at >= :dateFrom');
    replacements.dateFrom = new Date(params.dateFrom).toISOString();
  }
  if (params.dateTo) {
    whereClauses.push('cp.placed_at <= :dateTo');
    replacements.dateTo = new Date(params.dateTo).toISOString();
  }

  const whereSql = whereClauses.length > 0 ? `WHERE ${whereClauses.join(' AND ')}` : '';

  const countSql = `
    SELECT count(*) AS total
    FROM container_positions cp
    JOIN containers c ON c.id = cp.container_id
    JOIN yard_slots ys ON ys.id = cp.slot_id
    JOIN yard_blocks yb ON yb.id = ys.block_id
    LEFT JOIN users u ON u.id = cp.placed_by
    ${whereSql}
  `;
  const [countResult] = await sequelize.query(countSql, { replacements });
  const totalItems = parseInt(countResult[0]?.total || 0, 10);
  const totalPages = Math.ceil(totalItems / limit) || 1;

  const dataSql = `
    SELECT 
      cp.id,
      cp.placed_at AS "placedAt",
      cp.removed_at AS "removedAt",
      cp.is_current AS "isCurrent",
      c.id AS "containerId",
      c.container_no AS "containerNo",
      c.seal_no AS "sealNo",
      c.cargo_type::text AS "cargoType",
      c.status::text AS "containerStatus",
      c.gross_weight_kg AS "grossWeightKg",
      c.is_reefer AS "isReefer",
      ys.id AS "slotId",
      ys.bay,
      ys.row_no AS "row",
      ys.tier,
      ys.container_length_ft AS "containerLengthFt",
      ys.has_reefer_plug AS "hasReeferPlug",
      ys.status::text AS "slotStatus",
      yb.id AS "blockId",
      yb.code AS "blockCode",
      yb.name AS "blockName",
      yb.zone AS "blockZone",
      u.id AS "userId",
      u.username,
      u.full_name AS "fullName",
      u.role AS "userRole"
    FROM container_positions cp
    JOIN containers c ON c.id = cp.container_id
    JOIN yard_slots ys ON ys.id = cp.slot_id
    JOIN yard_blocks yb ON yb.id = ys.block_id
    LEFT JOIN users u ON u.id = cp.placed_by
    ${whereSql}
    ORDER BY cp.placed_at ${order}
    LIMIT :limit OFFSET :offset
  `;

  const [rows] = await sequelize.query(dataSql, { replacements });

  const items = rows.map((r, index) => {
    const slotCode = `${r.blockCode}.${String(r.bay).padStart(2, '0')}.${String(r.row).padStart(2, '0')}.${r.tier}`;
    const slotLabel = `Block ${r.blockCode} · Bay ${String(r.bay).padStart(2, '0')} · Row ${String(r.row).padStart(2, '0')} · Tier ${r.tier}`;
    const duration = formatDuration(r.placedAt, r.removedAt);

    return {
      index: offset + index + 1,
      id: r.id,
      placedAt: r.placedAt,
      removedAt: r.removedAt,
      isCurrent: r.isCurrent,
      duration,
      container: {
        id: r.containerId,
        containerNo: r.containerNo,
        sealNo: r.sealNo,
        cargoType: r.cargoType,
        status: r.containerStatus,
        grossWeightKg: r.grossWeightKg,
        isReefer: r.isReefer,
        sizeFt: r.containerLengthFt || 40,
      },
      slot: {
        id: r.slotId,
        slotCode,
        slotLabel,
        blockCode: r.blockCode,
        blockName: r.blockName,
        blockZone: r.blockZone,
        bay: r.bay,
        row: r.row,
        tier: r.tier,
        hasReeferPlug: r.hasReeferPlug,
        status: r.slotStatus,
      },
      placedBy: {
        id: r.userId || null,
        username: r.username || 'system',
        fullName: r.fullName || (r.username ? r.username : 'Điều phối Bãi Tiên Sa (Hệ thống)'),
        role: r.userRole || 'Yard Operator',
      },
    };
  });

  return {
    items,
    pagination: {
      page,
      limit,
      totalItems,
      totalPages,
      hasNext: page < totalPages,
      hasPrev: page > 1,
    },
    filterSummary: {
      search: params.search || null,
      slotCode: parsedSlot ? `${parsedSlot.block}.${String(parsedSlot.bay).padStart(2, '0')}.${String(parsedSlot.row).padStart(2, '0')}.${parsedSlot.tier}` : (params.slotCode || null),
      containerNo: params.containerNo || null,
    },
  };
}

/**
 * Xuất dữ liệu lịch sử Slot ra định dạng CSV (UTF-8 BOM hỗ trợ Excel tiếng Việt).
 */
async function exportSlotOccupancyHistoryCsv(params = {}) {
  const result = await getSlotOccupancyHistory({ ...params, page: 1, limit: 1000 });
  const items = result.items || [];

  const headers = [
    'STT',
    'Mã Lượt Lưu',
    'Số Container',
    'Loại Hàng',
    'Vị Trí Slot',
    'Block',
    'Bay',
    'Row',
    'Tier',
    'Thời Gian Vào (Bắt đầu)',
    'Thời Gian Rời (Kết thúc)',
    'Thời Lượng Lưu',
    'Trạng Thái',
    'Người Thực Hiện (Họ tên)',
    'Tài Khoản',
    'Vai Trò',
  ];

  const rows = items.map((item) => [
    item.index,
    item.id,
    item.container.containerNo,
    item.container.cargoType || 'Hàng tiêu chuẩn',
    item.slot.slotCode,
    item.slot.blockCode,
    String(item.slot.bay).padStart(2, '0'),
    String(item.slot.row).padStart(2, '0'),
    item.slot.tier,
    item.placedAt ? new Date(item.placedAt).toLocaleString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' }) : '',
    item.removedAt ? new Date(item.removedAt).toLocaleString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' }) : 'Đang lưu bãi',
    item.duration,
    item.isCurrent ? 'Đang chiếm slot (Hiện tại)' : 'Đã rời slot',
    item.placedBy.fullName,
    item.placedBy.username,
    item.placedBy.role,
  ]);

  const escapeCsv = (val) => {
    if (val == null) return '""';
    const str = String(val).replace(/"/g, '""');
    return `"${str}"`;
  };

  const csvContent = '\uFEFF' + [
    headers.map(escapeCsv).join(','),
    ...rows.map(row => row.map(escapeCsv).join(',')),
  ].join('\r\n');

  return csvContent;
}

/**
 * Ghi nhận thay đổi vị trí container (dành cho Yard Staff hoặc quy trình hạ bãi).
 */
async function recordContainerMovement(data = {}, userId = null) {
  const { sequelize } = require('../../config/database');
  const { containerNo, containerId, slotCode, slotId, notes } = data;

  if (!containerNo && !containerId) {
    const error = new Error('Vui lòng cung cấp số Container (containerNo) hoặc containerId.');
    error.status = 400;
    throw error;
  }
  if (!slotCode && !slotId) {
    const error = new Error('Vui lòng cung cấp mã Slot (slotCode) hoặc slotId.');
    error.status = 400;
    throw error;
  }

  let cRow;
  if (containerId && isUuid(containerId)) {
    const [c] = await sequelize.query('SELECT * FROM containers WHERE id = :id LIMIT 1', { replacements: { id: containerId } });
    cRow = c[0];
  } else if (containerNo) {
    const [c] = await sequelize.query('SELECT * FROM containers WHERE container_no ILIKE :no LIMIT 1', { replacements: { no: containerNo.trim() } });
    cRow = c[0];
  }
  if (!cRow) {
    const error = new Error(`Không tìm thấy container "${containerNo || containerId}".`);
    error.status = 404;
    throw error;
  }

  let sRow;
  if (slotId && isUuid(slotId)) {
    const [s] = await sequelize.query('SELECT * FROM yard_slots WHERE id = :id LIMIT 1', { replacements: { id: slotId } });
    sRow = s[0];
  } else if (slotCode) {
    const parsed = parseSlotCode(slotCode);
    if (!parsed) {
      const error = new Error(`Mã slot không hợp lệ: "${slotCode}". Định dạng chuẩn: Block.Bay.Row.Tier (VD: C01.06.01.1).`);
      error.status = 400;
      throw error;
    }
    const [s] = await sequelize.query(`
      SELECT ys.* FROM yard_slots ys
      JOIN yard_blocks yb ON yb.id = ys.block_id
      WHERE yb.code = :block AND ys.bay = :bay AND ys.row_no = :row AND ys.tier = :tier
      LIMIT 1
    `, { replacements: { block: parsed.block, bay: parsed.bay, row: parsed.row, tier: parsed.tier } });
    sRow = s[0];
  }
  if (!sRow) {
    const error = new Error(`Không tìm thấy slot "${slotCode || slotId}".`);
    error.status = 404;
    throw error;
  }

  return await sequelize.transaction(async (t) => {
    // Đánh dấu vị trí cũ đã rời
    await sequelize.query(`
      UPDATE container_positions
      SET is_current = false, removed_at = NOW()
      WHERE container_id = :containerId AND is_current = true
    `, { replacements: { containerId: cRow.id }, transaction: t });

    // Tạo bản ghi mới
    const [ins] = await sequelize.query(`
      INSERT INTO container_positions (id, container_id, slot_id, placed_by, placed_at, removed_at, is_current)
      VALUES (gen_random_uuid(), :containerId, :slotId, :userId, NOW(), NULL, true)
      RETURNING *
    `, {
      replacements: {
        containerId: cRow.id,
        slotId: sRow.id,
        userId: userId || null,
      },
      transaction: t,
    });

    await sequelize.query(`UPDATE yard_slots SET status = 'occupied' WHERE id = :slotId`, { replacements: { slotId: sRow.id }, transaction: t });
    await sequelize.query(`UPDATE containers SET status = 'in_yard' WHERE id = :containerId`, { replacements: { containerId: cRow.id }, transaction: t });

    return ins[0];
  });
}

/**
 * Thao tác Đảo chuyển container chắn sang ô tạm (Chiều Nhập 5A & Chiều Xuất 5B).
 */
async function unstackMove(id, { direction = 'inbound', targetSlot = 'B04-02-05-1', equipmentCode = 'RS-01', operatorName = 'Trần Văn Hùng', blockingContainerNo } = {}, userId) {
  if (direction === 'outbound') {
    const [cRows] = await sequelize.query(`SELECT container_no FROM export_booking_containers WHERE id = :id LIMIT 1`, { replacements: { id } });
    const contNo = cRows[0]?.container_no || 'NXPU6100011';

    await sequelize.query(
      `UPDATE export_booking_containers SET status = 'IN_PROGRESS' WHERE id = :id`,
      { replacements: { id } }
    );
    const taskCode = `TASK-UNSTACK-${Date.now().toString().slice(-6)}`;
    await sequelize.query(`
      INSERT INTO yard_tasks ("Id", "TaskCode", "OperationType", "ContainerNo", "BlockCode", "Priority", "Status", "EquipmentCode", "OperatorName", "CreatedAt", "IsDeleted")
      VALUES (gen_random_uuid(), :taskCode, 'Rehandle', :containerNo, 'B', 'High', 'Completed', :equipmentCode, :operatorName, NOW(), false)
    `, { replacements: { taskCode, containerNo: contNo, equipmentCode, operatorName } });

    return { id, status: 'ĐANG ĐẢO CHUYỂN', statusCode: 'IN_PROGRESS', blockingContainers: 0, tempSlotReserved: targetSlot };
  }

  const req = await findGateOutRequest(id);
  if (!req) {
    const err = new Error('Không tìm thấy yêu cầu xuất container.');
    err.statusCode = 404;
    throw err;
  }

  // 1. Xác định container chắn thực tế đè phía trên
  const blk = req.yard_block || 'B';
  const bay = parseInt(req.yard_bay, 10);
  const row = parseInt(req.yard_row, 10);
  const targetTier = parseInt(req.yard_tier, 10) || 1;

  let contToMove = blockingContainerNo;
  let fromTier = targetTier + 1;

  if (!contToMove && !isNaN(bay) && !isNaN(row)) {
    try {
      const [blockingRows] = await sequelize.query(`
        SELECT ys.tier, COALESCE(c.container_no, yt."ContainerNo", pyr.container_no, rc.container_no) AS container_no
        FROM yard_slots ys
        JOIN yard_blocks yb ON yb.id = ys.block_id
        LEFT JOIN container_positions cp ON cp.slot_id = ys.id AND cp.is_current = true
        LEFT JOIN containers c ON c.id = cp.container_id
        LEFT JOIN LATERAL (
          SELECT "ContainerNo" FROM yard_tasks
          WHERE ("IsDeleted" IS NOT TRUE OR "IsDeleted" IS NULL) AND ("Status" NOT IN ('GatedOut', 'Cancelled'))
            AND ("ToLocation" LIKE yb.code || '-' || LPAD(ys.bay::text, 2, '0') || '-' || LPAD(ys.row_no::text, 2, '0') || '-' || ys.tier::text || '%')
          LIMIT 1
        ) yt ON true
        LEFT JOIN persistent_yard_reservations pyr ON pyr.block_code = yb.code AND pyr.bay = ys.bay AND pyr.row_no = ys.row_no AND pyr.tier = ys.tier
        LEFT JOIN yard_slot_reservations ysr ON ysr.slot_id = ys.id AND ysr.reserved_to IS NULL
        LEFT JOIN containers rc ON rc.id = ysr.container_id
        WHERE yb.code = :blk AND ys.bay = :bay AND ys.row_no = :row AND ys.tier > :targetTier
        ORDER BY ys.tier DESC
      `, { replacements: { blk, bay, row, targetTier } });

      const realBlockingConts = (blockingRows || []).filter(r => r.container_no);
      if (realBlockingConts.length > 0) {
        contToMove = realBlockingConts[0].container_no;
        fromTier = realBlockingConts[0].tier;
      }
    } catch (e) {
      console.warn('[Unstack] Error detecting blocking cont:', e.message);
    }
  }

  // Nếu không phát hiện cont cụ thể, dùng cont chắn mẫu chuẩn của bãi
  if (!contToMove) {
    contToMove = targetTier === 1 ? 'CAIU5506483' : 'TEMU4451923';
  }

  const fromLocation = `${blk}-${String(bay).padStart(2, '0')}-${String(row).padStart(2, '0')}-${fromTier}`;

  // 2. Cập nhật yêu cầu xuất container: Giải phóng chướng ngại vật
  req.blocking_containers = 0;
  req.status = 'IN_PROGRESS';
  req.flow_step = Math.max(req.flow_step || 0, 1);
  req.note = (req.note ? req.note + ' · ' : '') + `Đã đảo chuyển cont chắn ${contToMove} sang ô tạm ${targetSlot}`;
  await req.save();

  // 3. Ghi nhận Task Rehandle trong yard_tasks cho đúng Container Chắn
  const taskCode = `TASK-UNSTACK-${Date.now().toString().slice(-6)}`;
  try {
    await sequelize.query(`
      INSERT INTO yard_tasks ("Id", "TaskCode", "OperationType", "ContainerNo", "FromLocation", "ToLocation", "BlockCode", "Priority", "Status", "EquipmentCode", "OperatorName", "CreatedAt", "IsDeleted", "Notes")
      VALUES (gen_random_uuid(), :taskCode, 'Rehandle', :containerNo, :fromLoc, :toLoc, :blockCode, 'High', 'Completed', :equipmentCode, :operatorName, NOW(), false, :notes)
    `, {
      replacements: {
        taskCode,
        containerNo: contToMove,
        fromLoc: fromLocation,
        toLoc: targetSlot,
        blockCode: blk,
        equipmentCode: equipmentCode || req.rtg_crane || 'RS-01',
        operatorName: operatorName || req.rtg_operator || 'Trần Văn Hùng',
        notes: `Quy trình đảo chuyển chặt chẽ: Di chuyển mở đường giải phóng container ${req.container_id}`,
      }
    });

    // 4. Cập nhật vị trí lưu bãi của Container Chắn sang ô tạm mới trong persistent_yard_reservations
    const targetParts = String(targetSlot).split(/[-.]/);
    if (targetParts.length >= 4) {
      const tBlk = targetParts[0];
      const tBay = parseInt(targetParts[1], 10);
      const tRow = parseInt(targetParts[2], 10);
      const tTier = parseInt(targetParts[3], 10);

      const jsonData = JSON.stringify({
        containerNo: contToMove,
        block: tBlk,
        bay: tBay,
        row: tRow,
        tier: tTier,
        status: 'reserved',
        carrier: 'COSCO / Hapag-Lloyd',
      });

      await sequelize.query(`
        INSERT INTO persistent_yard_reservations (id, container_no, block_code, bay, row_no, tier, size, status, data, updated_at)
        VALUES (:id, :contNo, :tBlk, :tBay, :tRow, :tTier, 40, 'reserved', :jsonData::jsonb, NOW())
        ON CONFLICT (id) DO UPDATE SET
          block_code = EXCLUDED.block_code,
          bay = EXCLUDED.bay,
          row_no = EXCLUDED.row_no,
          tier = EXCLUDED.tier,
          status = 'reserved',
          data = EXCLUDED.data,
          updated_at = NOW()
      `, {
        replacements: {
          id: `RES-UNSTACK-${contToMove}`,
          contNo: contToMove,
          tBlk,
          tBay,
          tRow,
          tTier,
          jsonData,
        }
      });

      // Xóa các đặt chỗ cũ của container chắn ở vị trí cũ để giải phóng slot
      await sequelize.query(`
        DELETE FROM persistent_yard_reservations
        WHERE container_no = :contNo AND id != :id
      `, { replacements: { contNo: contToMove, id: `RES-UNSTACK-${contToMove}` } });

      // Đóng reservation cũ trong yard_slot_reservations
      await sequelize.query(`
        UPDATE yard_slot_reservations ysr
        SET reserved_to = NOW()
        FROM containers c
        WHERE ysr.container_id = c.id
          AND c.container_no = :contNo
          AND ysr.reserved_to IS NULL
      `, { replacements: { contNo: contToMove } });

      // Cập nhật vị trí mới nếu cont chắn cũng có bản ghi yêu cầu xuất bãi
      await YardGateOutRequest.update({
        yard_block: tBlk,
        yard_bay: String(tBay).padStart(2, '0'),
        yard_row: String(tRow).padStart(2, '0'),
        yard_tier: String(tTier),
        yard_slot: targetSlot,
        note: `Đã đảo chuyển từ ${fromLocation} sang ô đệm ${targetSlot}`,
      }, {
        where: { container_id: contToMove }
      });

      // Giải phóng vị trí cũ trong container_positions
      await sequelize.query(`
        UPDATE container_positions cp
        SET is_current = false
        FROM containers c
        WHERE cp.container_id = c.id AND c.container_no = :contNo
      `, { replacements: { contNo: contToMove } });
    }
  } catch (err) {
    console.warn('[UnstackMove] DB log warning:', err.message);
  }

  let slotMap = null;
  try {
    const { getLayoutSnapshot } = require('./yard-layout');
    const snapshot = await getLayoutSnapshot();
    slotMap = new Map();
    for (const s of (snapshot?.slots || [])) {
      if (s.containerNumber || s.containerId) {
        const k1 = `${s.block}:${s.bay}:${s.row}`;
        const k2 = `${String(s.block).replace(/^B0?/, 'B')}:${s.bay}:${s.row}`;
        if (!slotMap.has(k1)) slotMap.set(k1, new Map());
        slotMap.get(k1).set(s.tier, s);
        if (k1 !== k2) {
          if (!slotMap.has(k2)) slotMap.set(k2, new Map());
          slotMap.get(k2).set(s.tier, s);
        }
      }
    }
  } catch (err) {
    console.warn('[UnstackMove] Failed to build slotMap:', err.message);
  }

  const formatted = formatGateOutRequest(req, slotMap);
  formatted.unstackedContainer = contToMove;
  formatted.unstackedFrom = fromLocation;
  formatted.unstackedTo = targetSlot;
  formatted.unstackTaskCode = taskCode;

  emitYardFlowStepUpdated(formatted);
  return formatted;
}

/**
 * Đưa container ra vị trí bãi đệm gần đường xe chạy (Bước 6 Chiều Nhập 5A).
 */
async function moveToBufferStage(id, { bufferSlot = 'BUFFER-GATE-01' } = {}, userId) {
  const req = await findGateOutRequest(id);
  if (!req) {
    const err = new Error('Không tìm thấy yêu cầu xuất container.');
    err.statusCode = 404;
    throw err;
  }

  const oldPos = req.yard_slot;
  req.yard_slot = bufferSlot;
  req.yard_block = 'BUFFER';
  req.flow_step = Math.max(req.flow_step || 0, 2);
  await req.save();

  const taskCode = `TASK-STAGE-${Date.now().toString().slice(-6)}`;
  await sequelize.query(`
    INSERT INTO yard_tasks ("Id", "TaskCode", "OperationType", "ContainerNo", "FromLocation", "ToLocation", "BlockCode", "Priority", "Status", "CreatedAt", "IsDeleted")
    VALUES (gen_random_uuid(), :taskCode, 'Stage', :containerNo, :fromLoc, :toLoc, 'BUFFER', 'Normal', 'Completed', NOW(), false)
  `, {
    replacements: {
      taskCode,
      containerNo: req.container_id,
      fromLoc: oldPos,
      toLoc: bufferSlot,
    }
  });

  return formatGateOutRequest(req);
}

/**
 * Chốt Sẵn Sàng Giao (Bước 8 Chiều Nhập 5A) sau khi đối soát e-D/O & số chì.
 */
async function confirmReadyForDelivery(id, { verifiedContainerId = true, verifiedSeal = true, condition = 'Tốt (Nguyên vẹn)', note } = {}, userId) {
  const req = await findGateOutRequest(id);
  if (!req) {
    const err = new Error('Không tìm thấy yêu cầu xuất container.');
    err.statusCode = 404;
    throw err;
  }

  req.status = 'READY_FOR_GATE_OUT';
  req.flow_step = 4;
  req.verified_container_id = Boolean(verifiedContainerId);
  req.verified_seal = Boolean(verifiedSeal);
  req.condition = condition;
  if (note) req.note = note;
  await req.save();

  const formatted = formatGateOutRequest(req);
  emitContainerReadyForGateOut(formatted);

  return formatted;
}

/**
 * Chốt Sẵn Sàng Xếp Tàu (Chiều Xuất 5B) khi đủ 5 điều kiện tiên quyết.
 */
async function confirmReadyForLoading(id, { loadingSequence, quayCrane } = {}, userId) {
  await sequelize.query(
    `UPDATE export_booking_containers SET status = 'READY_FOR_LOADING' WHERE id = :id`,
    { replacements: { id } }
  );

  return {
    id,
    status: 'SẴN SÀNG XẾP TÀU',
    statusCode: 'READY_FOR_LOADING',
    blockingContainers: 0,
    quayCrane: quayCrane || 'QC-01',
    loadingSequence: loadingSequence || 1,
  };
}

/**
 * Điều động xe nội bộ kéo container ra chân cẩu cầu tàu (Chiều Xuất 5B).
 */
async function dispatchApronTruck(id, { truckPlate = 'TRK-TERMINAL-01', quayCrane = 'QC-01' } = {}, userId) {
  const [cRows] = await sequelize.query(`SELECT container_no FROM export_booking_containers WHERE id = :id LIMIT 1`, { replacements: { id } });
  const contNo = cRows[0]?.container_no || 'NXPU6100011';

  const taskCode = `TASK-APRON-${Date.now().toString().slice(-6)}`;
  await sequelize.query(`
    INSERT INTO yard_tasks ("Id", "TaskCode", "OperationType", "ContainerNo", "BlockCode", "Priority", "VehiclePlate", "EquipmentCode", "Status", "CreatedAt", "IsDeleted")
    VALUES (gen_random_uuid(), :taskCode, 'LoadApron', :containerNo, 'B', 'High', :truckPlate, :quayCrane, 'In_Progress', NOW(), false)
  `, { replacements: { taskCode, containerNo: contNo, truckPlate, quayCrane } });

  return {
    id,
    success: true,
    truckPlate,
    quayCrane,
    message: `Đã điều động xe nội bộ ${truckPlate} kéo container ra chân cẩu ${quayCrane}.`,
  };
}

/**
 * Ghi nhận ngắt điện / cắm lại container lạnh kèm Dwell timer (30-45 phút).
 */
async function toggleReeferPower(id, { isPlugged = false, dwellMinutes = 45 } = {}, userId) {
  return {
    id,
    isPlugged,
    dwellMinutes,
    status: isPlugged ? 'Đang cắm điện bãi' : 'Tạm ngắt điện di chuyển',
    unpluggedMinutes: isPlugged ? 0 : 5,
    maxUnplugMinutes: dwellMinutes,
  };
}

/**
 * Hủy lệnh chuẩn bị, cờ giữ (Hold) hoặc Shut-Out phút chót.
 */
async function cancelPreparationOrShutOut(id, { reason = 'Cờ giữ từ hãng tàu / hải quan', category = 'Hold', direction = 'inbound' } = {}, userId) {
  if (direction === 'outbound') {
    await sequelize.query(`
      UPDATE export_booking_containers 
      SET status = 'ShutOut', exclusion_reason = :reason 
      WHERE id = :id
    `, { replacements: { id, reason } });

    return {
      id,
      success: true,
      statusCode: 'CANCELLED_SHUT_OUT',
      status: 'ĐÃ RÚT KHỎI DANH SÁCH (SHUT-OUT)',
      reason,
      chargeParty: 'Hãng tàu (Carrier)',
    };
  }

  const req = await findGateOutRequest(id);
  if (req) {
    req.status = 'CANCELLED';
    req.note = reason;
    await req.save();
  }

  return {
    id,
    success: true,
    statusCode: 'CANCELLED_HOLD',
    status: 'ĐÃ HỦY LỆNH (HOLD)',
    reason,
  };
}

module.exports = {
  getGateOutRequests,
  getOutboundPreparationRequests,
  getGateOutRequestById,
  createLiftOrder,
  updateFlowStep,
  confirmLoadedOnTruck,
  createGateOutRequest,
  formatGateOutRequest,
  getSlotOccupancyHistory,
  exportSlotOccupancyHistoryCsv,
  recordContainerMovement,
  parseSlotCode,
  unstackMove,
  moveToBufferStage,
  confirmReadyForDelivery,
  confirmReadyForLoading,
  dispatchApronTruck,
  toggleReeferPower,
  cancelPreparationOrShutOut,
};


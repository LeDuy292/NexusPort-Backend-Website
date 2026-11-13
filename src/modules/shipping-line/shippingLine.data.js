'use strict';

/**
 * Kho dữ liệu khởi tạo nghiệp vụ Hãng tàu (NexusPort Shipping Line)
 * Dữ liệu sát với thực tế vận hành hãng tàu tại Cảng Tiên Sa (Đà Nẵng), Cái Mép, Hải Phòng.
 */

// 1. Danh sách Tàu (Vessel)
let vessels = [
  {
    id: 'ves-01',
    vesselName: 'EVER GLORY',
    imoNumber: '9811000',
    callSign: '3FDA8',
    flag: 'Panama (PA)',
    vesselType: 'Container Ship',
    capacityTeu: 20150,
    status: 'ACTIVE',
  },
  {
    id: 'ves-02',
    vesselName: 'ONE CONTINUITY',
    imoNumber: '9833412',
    callSign: '7KBD2',
    flag: 'Japan (JP)',
    vesselType: 'Container Ship',
    capacityTeu: 14000,
    status: 'ACTIVE',
  },
  {
    id: 'ves-03',
    vesselName: 'CMA CGM CHENNAI',
    imoNumber: '9785461',
    callSign: '9V551',
    flag: 'France (FR)',
    vesselType: 'Container Ship',
    capacityTeu: 10500,
    status: 'ACTIVE',
  },
  {
    id: 'ves-04',
    vesselName: 'MAERSK MC-KINNEY MOLLER',
    imoNumber: '9619907',
    callSign: 'OWDQ2',
    flag: 'Denmark (DK)',
    vesselType: 'Container Ship',
    capacityTeu: 18270,
    status: 'ACTIVE',
  },
  {
    id: 'ves-05',
    vesselName: 'WAN HAI 502',
    imoNumber: '9326419',
    callSign: 'BK882',
    flag: 'Singapore (SG)',
    vesselType: 'Feeder Container',
    capacityTeu: 4500,
    status: 'ACTIVE',
  },
];

// 2. Danh sách Chuyến tàu (Voyage)
let voyages = [
  {
    id: 'voy-01',
    voyageNumber: 'EG-2605W',
    vesselId: 'ves-01',
    vesselName: 'EVER GLORY',
    pol: 'Cảng Tiên Sa, Đà Nẵng (VNDAD)',
    pod: 'Tokyo, Nhật Bản (JPTYO)',
    eta: '2026-11-08 14:00',
    etd: '2026-11-09 22:00',
    ata: '2026-11-08 13:45',
    atd: null,
    cyCutOff: '2026-11-08 18:00',
    vgmCutOff: '2026-11-08 10:00',
    status: 'BERTHED', // SCHEDULED, IN_TRANSIT, BERTHED, OPERATING, DEPARTED, COMPLETED
    terminal: 'Bến số 1 (TS-01) - Cảng Tiên Sa',
    remarks: 'Tàu cập đúng kế hoạch, đang làm hàng cầu bờ STS-01 và STS-02',
    manifestCount: 2,
    containerCount: 420,
  },
  {
    id: 'voy-02',
    voyageNumber: 'OC-2612E',
    vesselId: 'ves-02',
    vesselName: 'ONE CONTINUITY',
    pol: 'Cái Mép, Bà Rịa - Vũng Tàu (VNCMT)',
    pod: 'Los Angeles, Hoa Kỳ (USLAX)',
    eta: '2026-11-11 08:00',
    etd: '2026-11-12 18:00',
    ata: null,
    atd: null,
    cyCutOff: '2026-11-10 20:00',
    vgmCutOff: '2026-11-10 12:00',
    status: 'IN_TRANSIT',
    terminal: 'Cầu bến TCIT Cái Mép',
    remarks: 'Hành trình luồng biển Vũng Tàu, thời tiết thuận lợi',
    manifestCount: 1,
    containerCount: 310,
  },
  {
    id: 'voy-03',
    voyageNumber: 'CM-2644W',
    vesselId: 'ves-03',
    vesselName: 'CMA CGM CHENNAI',
    pol: 'Cảng Tiên Sa, Đà Nẵng (VNDAD)',
    pod: 'Rotterdam, Hà Lan (NLRTM)',
    eta: '2026-11-14 06:00',
    etd: '2026-11-15 16:00',
    ata: null,
    atd: null,
    cyCutOff: '2026-11-13 14:00',
    vgmCutOff: '2026-11-13 08:00',
    status: 'SCHEDULED',
    terminal: 'Bến số 2 (TS-02) - Cảng Tiên Sa',
    remarks: 'Mở tiếp nhận container xuất từ 09/11/2026',
    manifestCount: 1,
    containerCount: 250,
  },
  {
    id: 'voy-04',
    voyageNumber: 'MM-2601S',
    vesselId: 'ves-04',
    vesselName: 'MAERSK MC-KINNEY MOLLER',
    pol: 'Hải Phòng (VNHPH)',
    pod: 'Tanjung Pelepas, Malaysia (MYTPP)',
    eta: '2026-11-06 10:00',
    etd: '2026-11-07 15:00',
    ata: '2026-11-06 09:30',
    atd: null,
    cyCutOff: '2026-11-06 04:00',
    vgmCutOff: '2026-11-05 22:00',
    status: 'OPERATING',
    terminal: 'Cảng Quốc tế Lạch Huyện (HICT)',
    remarks: 'Đang dỡ hàng nhập khẩu, tiến độ 65%',
    manifestCount: 1,
    containerCount: 580,
  },
];

// Lịch sử điều chỉnh Cut-off của chuyến tàu
let cutOffHistory = [
  {
    id: 'coh-01',
    voyageNumber: 'EG-2605W',
    field: 'CY Cut-off',
    oldValue: '2026-11-08 16:00',
    newValue: '2026-11-08 18:00',
    reason: 'Hỗ trợ đoàn xe hạ bãi container lạnh từ miền Tây bị ùn tắc cao tốc',
    updatedBy: 'shipping01',
    timestamp: '2026-11-06 14:20:00',
  },
];

// 3. Danh sách Vận đơn (Bill of Lading - B/L)
let billsOfLading = [];

// 3b. Danh sách Hướng dẫn gửi hàng (Shipping Instructions - SI) cho hàng xuất khẩu
let shippingInstructions = [];

// 4. Danh sách Bản khai Hàng hóa (Manifest)
let manifests = [];

// 5. Danh sách Lệnh Giao Hàng Điện Tử (eDO)
let deliveryOrders = [];

// 6. Dữ liệu tiếp nhận từ Cảng phục vụ đối soát Manifest (Reconciliation Dataset)
// Container đã qua cổng (GATE_IN_COMPLETED) có số seal và trạm cân ghi nhận.
// Container chưa tới cổng (PENDING_GATE_IN) hiển thị trạng thái "Chờ cảng", không so sánh sai lệch.
let portReconciliationRecords = [];

// Bản sao trạng thái ban đầu để hỗ trợ khôi phục / nạp dữ liệu mẫu nhanh (Seed Reset)
const INITIAL_STATE = JSON.stringify({
  vessels,
  voyages,
  cutOffHistory,
  billsOfLading,
  shippingInstructions,
  manifests,
  deliveryOrders,
  portReconciliationRecords,
});

function resetData() {
  const fresh = JSON.parse(INITIAL_STATE);
  vessels.length = 0; vessels.push(...fresh.vessels);
  voyages.length = 0; voyages.push(...fresh.voyages);
  cutOffHistory.length = 0; cutOffHistory.push(...fresh.cutOffHistory);
  billsOfLading.length = 0; billsOfLading.push(...fresh.billsOfLading);
  shippingInstructions.length = 0; shippingInstructions.push(...fresh.shippingInstructions);
  manifests.length = 0; manifests.push(...fresh.manifests);
  deliveryOrders.length = 0; deliveryOrders.push(...fresh.deliveryOrders);
  portReconciliationRecords.length = 0; portReconciliationRecords.push(...fresh.portReconciliationRecords);
  return {
    vesselsCount: vessels.length,
    voyagesCount: voyages.length,
    billsCount: billsOfLading.length,
    siCount: shippingInstructions.length,
    manifestsCount: manifests.length,
    deliveryOrdersCount: deliveryOrders.length,
  };
}

module.exports = {
  vessels,
  voyages,
  cutOffHistory,
  billsOfLading,
  shippingInstructions,
  manifests,
  deliveryOrders,
  portReconciliationRecords,
  resetData,
};


'use strict';

/**
 * Shipping Line Business Service for NexusPort
 * Hỗ trợ toàn bộ nghiệp vụ Quản lý Hãng tàu:
 * 1. Booking Xuất khẩu (Quy trình 6 bước: Đặt chỗ -> Kiểm tra tàu -> Cấp lệnh lấy vỏ -> Khai VGM & SI -> Hạ bãi -> Phát hành B/L)
 * 2. Hàng Nhập khẩu & Manifest (Lược khai hàng hóa đầy đủ chuẩn hải quan & cảng)
 * 3. Vận đơn (Bill of Lading) & Lệnh Giao Hàng Điện Tử (eDO)
 * 4. Quản lý vỏ container, Depot & Biểu cước DEM/DET lũy tiến
 * 5. Tác nghiệp đồng bộ EDI Cảng (COPRAR, COARRI, CODECO, BAPLIE, VERMAS)
 */

// ── In-Memory Database Seed Data ─────────────────────────────────────────────

let bookings = [
  {
    id: 'bkg-001',
    bookingNumber: 'BKG-2026-0891',
    shipperName: 'Công ty Cổ phần Thủy sản Minh Phú',
    vesselName: 'EVER GLORY',
    voyageNumber: 'EG-2605W',
    destinationPort: 'Tokyo, Nhật Bản (JPTYO)',
    containerType: '40RF',
    containerQuantity: 4,
    etd: '05/11/2026 14:00',
    status: 'PENDING',
    depotPickUp: 'Depot Tân Cảng Suối Tiên',
    freeDemDays: 5,
    freeDetDays: 3,
    cutOffs: [
      { code: 'SI_CUTOFF', label: 'SI Cut-off (Chốt chi tiết B/L)', deadline: '03/11/2026 12:00', isPassed: false },
      { code: 'VGM_CUTOFF', label: 'VGM Cut-off (Xác nhận tải trọng)', deadline: '04/11/2026 10:00', isPassed: false },
      { code: 'CY_CUTOFF', label: 'CY Cut-off (Cắt máng bến cảng)', deadline: '04/11/2026 18:00', isPassed: false },
    ],
    containers: [
      { id: 'c1', containerNumber: 'EMCU9284710', containerType: '40RF', sealNumber: 'SL-88412', vgmWeight: 28400, status: 'CHO_DONG' },
      { id: 'c2', containerNumber: 'EMCU9284725', containerType: '40RF', sealNumber: 'SL-88413', vgmWeight: 28250, status: 'CHO_DONG' },
    ],
    history: [
      { id: 'h1', timestamp: '01/11/2026 09:15', actor: 'Shipper', action: 'TẠO_BOOKING', description: 'Gửi yêu cầu đặt 4x40RF thủy sản đông lạnh đi Tokyo' },
    ],
  },
  {
    id: 'bkg-002',
    bookingNumber: 'BKG-2026-0892',
    shipperName: 'Tập đoàn Dệt may Việt Nam (Vinatex)',
    vesselName: 'ONE CONTINUITY',
    voyageNumber: 'OC-2612E',
    destinationPort: 'Los Angeles, Mỹ (USLAX)',
    containerType: '40HC',
    containerQuantity: 10,
    etd: '07/11/2026 20:00',
    status: 'APPROVED',
    depotPickUp: 'Depot ICD Phước Long 3',
    freeDemDays: 7,
    freeDetDays: 5,
    cutOffs: [
      { code: 'SI_CUTOFF', label: 'SI Cut-off', deadline: '05/11/2026 17:00', isPassed: false },
      { code: 'VGM_CUTOFF', label: 'VGM Cut-off', deadline: '06/11/2026 12:00', isPassed: false },
      { code: 'CY_CUTOFF', label: 'CY Cut-off (Cắt máng cảng)', deadline: '06/11/2026 22:00', isPassed: false },
    ],
    containers: [
      { id: 'c3', containerNumber: 'ONEY5109823', containerType: '40HC', sealNumber: 'ON-91201', vgmWeight: 18500, status: 'DA_HA_BAI' },
      { id: 'c4', containerNumber: 'ONEY5109839', containerType: '40HC', sealNumber: 'ON-91202', vgmWeight: 19100, status: 'DA_HA_BAI' },
    ],
    history: [
      { id: 'h2', timestamp: '31/10/2026 14:20', actor: 'Shipper', action: 'TẠO_BOOKING', description: 'Đăng ký chỗ 10x40HC hàng may mặc xuất khẩu US' },
      { id: 'h3', timestamp: '01/11/2026 08:30', actor: 'Hãng tàu ONE', action: 'DUYỆT_BOOKING', description: 'Đã duyệt chỗ, cấp lệnh lấy vỏ rỗng tại ICD Phước Long 3' },
    ],
  },
  {
    id: 'bkg-003',
    bookingNumber: 'BKG-2026-0890',
    shipperName: 'Công ty TNHH Cà phê Trung Nguyên',
    vesselName: 'CMA CGM CHENNAI',
    voyageNumber: 'CM-2644W',
    destinationPort: 'Rotterdam, Hà Lan (NLRTM)',
    containerType: '20GP',
    containerQuantity: 6,
    etd: '03/11/2026 18:00',
    status: 'BL_ISSUED',
    blNumber: 'CMA2026RTM8891',
    depotPickUp: 'Depot Transimex Sài Gòn',
    freeDemDays: 7,
    freeDetDays: 7,
    cutOffs: [
      { code: 'SI_CUTOFF', label: 'SI Cut-off', deadline: '01/11/2026 12:00', isPassed: true },
      { code: 'VGM_CUTOFF', label: 'VGM Cut-off', deadline: '02/11/2026 10:00', isPassed: true },
      { code: 'CY_CUTOFF', label: 'CY Cut-off (Cắt máng cảng)', deadline: '02/11/2026 18:00', isPassed: true },
    ],
    containers: [
      { id: 'c5', containerNumber: 'CMAU1982734', containerType: '20GP', sealNumber: 'CM-77123', vgmWeight: 21800, status: 'DA_XEP_TAU' },
      { id: 'c6', containerNumber: 'CMAU1982740', containerType: '20GP', sealNumber: 'CM-77124', vgmWeight: 21950, status: 'DA_XEP_TAU' },
    ],
    history: [
      { id: 'h4', timestamp: '28/10/2026 10:00', actor: 'Shipper', action: 'TẠO_BOOKING', description: 'Yêu cầu 6x20GP đóng cà phê nhân' },
      { id: 'h5', timestamp: '28/10/2026 14:00', actor: 'Hãng tàu CMA CGM', action: 'DUYỆT_BOOKING', description: 'Duyệt chỗ và cấp vỏ' },
      { id: 'h6', timestamp: '01/11/2026 16:30', actor: 'Hệ thống Cảng', action: 'XẾP_LÊN_TÀU', description: 'Container đã bốc lên tàu an toàn' },
      { id: 'h7', timestamp: '02/11/2026 09:00', actor: 'Hãng tàu CMA CGM', action: 'PHÁT_HÀNH_BL', description: 'Hoàn thiện vận đơn gốc CMA2026RTM8891' },
    ],
  },
  {
    id: 'bkg-004',
    bookingNumber: 'BKG-2026-0888',
    shipperName: 'Công ty TNHH Gỗ Đức Thành',
    vesselName: 'KMTC INCHEON',
    voyageNumber: 'KI-2602N',
    destinationPort: 'Busan, Hàn Quốc (KRPUS)',
    containerType: '20GP',
    containerQuantity: 3,
    etd: '04/11/2026 10:00',
    status: 'REJECTED',
    rejectionReason: 'Tàu đã đầy tải tải trọng phân bổ (Deadweight capacity full)',
    history: [
      { id: 'h8', timestamp: '29/10/2026 15:45', actor: 'Shipper', action: 'TẠO_BOOKING', description: 'Đề nghị cấp 3x20GP đi Busan' },
      { id: 'h9', timestamp: '30/10/2026 09:00', actor: 'Hãng tàu KMTC', action: 'TỪ_CHỐI_BOOKING', description: 'Từ chối: Tàu đã đầy tải trọng phân bổ' },
    ],
  },
];

let voyages = [
  {
    id: 'v-01',
    vesselName: 'EVER GLORY',
    voyageNumber: 'EG-2605W',
    eta: '02/11/2026 08:00',
    ata: '02/11/2026 07:30',
    berthCode: 'Cầu bến B1 - Tân Cảng Cát Lái',
    totalBL: 4,
    totalContainers: 148,
    manifestUploaded: true,
  },
  {
    id: 'v-02',
    vesselName: 'ONE CONTINUITY',
    voyageNumber: 'OC-2612E',
    eta: '04/11/2026 14:00',
    ata: undefined,
    berthCode: 'Cầu bến B3 - Tân Cảng Cát Lái',
    totalBL: 8,
    totalContainers: 320,
    manifestUploaded: true,
  },
  {
    id: 'v-03',
    vesselName: 'MAERSK MC-KINNEY',
    voyageNumber: 'MK-2601S',
    eta: '06/11/2026 06:00',
    ata: undefined,
    berthCode: 'Cầu bến B2 - Cảng Cái Mép',
    totalBL: 12,
    totalContainers: 540,
    manifestUploaded: false,
  },
];

let blList = [
  {
    id: 'bl-001',
    blNumber: 'MSK2026VN009182',
    voyageNumber: 'EG-2605W',
    vesselName: 'EVER GLORY',
    consignee: 'Công ty Cổ phần Thép Pomina',
    consigneeName: 'Công ty Cổ phần Thép Pomina',
    shipper: 'POSCO Steel Co., Ltd (Pohang, Korea)',
    notifyParty: 'Pomina Steel Logistics Hub (KCN Phú Mỹ, BR-VT)',
    pol: 'Pohang, Hàn Quốc (KRPOH)',
    pod: 'Cát Lái, Hồ Chí Minh (VNCLI)',
    cargoDescription: 'Thép cuộn cán nóng nguyên khối (Hot Rolled Steel Coils in Containers)',
    hsCode: '7208.38.00',
    packagesCount: 24,
    packageType: 'Cuộn đai thép (Steel Coils on Cradles)',
    grossWeightKg: 58400,
    cbm: 38.5,
    freightTerms: 'PREPAID',
    blType: 'ORIGINAL',
    containerCount: 2,
    containersCount: 2,
    status: 'PENDING_DO',
    eta: '02/11/2026 08:00',
    charges: [
      { id: 'ch-1', chargeName: 'Ocean Freight (Cước biển quốc tế)', amount: 1850, currency: 'USD', isPaid: true, paidAt: '01/11/2026 14:20' },
      { id: 'ch-2', chargeName: 'Terminal Handling Charge (THC đến)', amount: 5200000, currency: 'VND', isPaid: true, paidAt: '02/11/2026 09:10' },
      { id: 'ch-3', chargeName: 'Phí Lệnh giao hàng (D/O Fee)', amount: 880000, currency: 'VND', isPaid: true, paidAt: '02/11/2026 09:10' },
      { id: 'ch-4', chargeName: 'Phí vệ sinh container (Cleaning fee)', amount: 660000, currency: 'VND', isPaid: true, paidAt: '02/11/2026 09:10' },
    ],
    containers: [
      {
        id: 'cnt-1',
        containerNumber: 'MSKU9284710',
        containerType: '20GP',
        sealNumber: 'MSK-771239',
        weightKg: 29200,
        tareWeightKg: 2200,
        cargoType: 'Thép cuộn đặc dụng',
        status: 'DISCHARGED',
        dischargedAt: '02/11/2026 10:15',
      },
      {
        id: 'cnt-2',
        containerNumber: 'MSKU9284725',
        containerType: '20GP',
        sealNumber: 'MSK-771240',
        weightKg: 29200,
        tareWeightKg: 2200,
        cargoType: 'Thép cuộn đặc dụng',
        status: 'DISCHARGED',
        dischargedAt: '02/11/2026 10:30',
      },
    ],
  },
  {
    id: 'bl-002',
    blNumber: 'ONE2026SGN7712',
    voyageNumber: 'EG-2605W',
    vesselName: 'EVER GLORY',
    consignee: 'Công ty Cổ phần Sữa Việt Nam (Vinamilk)',
    consigneeName: 'Công ty Cổ phần Sữa Việt Nam (Vinamilk)',
    shipper: 'Fonterra Co-operative Group Limited (Auckland, New Zealand)',
    notifyParty: 'Same as Consignee (Phòng Xuất Nhập Khẩu Vinamilk)',
    pol: 'Auckland, New Zealand (NZAKL)',
    pod: 'Cát Lái, Hồ Chí Minh (VNCLI)',
    cargoDescription: 'Sữa bột nguyên kem công nghiệp (Whole Milk Powder)',
    hsCode: '0402.21.00',
    packagesCount: 800,
    packageType: 'Bao 25kg trên pallet nhựa (25kg bags on plastic pallets)',
    grossWeightKg: 42000,
    cbm: 68.2,
    freightTerms: 'COLLECT',
    blType: 'TELEX_RELEASE',
    containerCount: 2,
    containersCount: 2,
    status: 'DO_ISSUED',
    eta: '02/11/2026 08:00',
    charges: [
      { id: 'ch-5', chargeName: 'Cước vận chuyển quốc tế (Collect)', amount: 2400, currency: 'USD', isPaid: true, paidAt: '02/11/2026 11:00' },
      { id: 'ch-6', chargeName: 'Terminal Handling Charge (THC đến)', amount: 7600000, currency: 'VND', isPaid: true, paidAt: '02/11/2026 11:00' },
      { id: 'ch-7', chargeName: 'Phí phát hành eDO', amount: 880000, currency: 'VND', isPaid: true, paidAt: '02/11/2026 11:00' },
    ],
    containers: [
      {
        id: 'cnt-3',
        containerNumber: 'ONEY4120934',
        containerType: '40HC',
        sealNumber: 'ONE-88123',
        weightKg: 21000,
        tareWeightKg: 3800,
        cargoType: 'Hàng thực phẩm khô',
        status: 'DISCHARGED',
        dischargedAt: '02/11/2026 11:45',
      },
      {
        id: 'cnt-4',
        containerNumber: 'ONEY4120955',
        containerType: '40HC',
        sealNumber: 'ONE-88124',
        weightKg: 21000,
        tareWeightKg: 3800,
        cargoType: 'Hàng thực phẩm khô',
        status: 'DISCHARGED',
        dischargedAt: '02/11/2026 12:00',
      },
    ],
  },
  {
    id: 'bl-003',
    blNumber: 'EMC2026TYO5541',
    voyageNumber: 'EG-2605W',
    vesselName: 'EVER GLORY',
    consignee: 'Tập đoàn Điện tử Samsung Vina',
    consigneeName: 'Tập đoàn Điện tử Samsung Vina',
    shipper: 'Samsung Electronics Corp. (Suwon, Korea)',
    notifyParty: 'Samsung C&T Logistics Vietnam',
    pol: 'Incheon, Hàn Quốc (KRINC)',
    pod: 'Cát Lái, Hồ Chí Minh (VNCLI)',
    cargoDescription: 'Linh kiện điện tử bán dẫn & vi mạch IC tinh xảo (Precision Electronic IC Parts)',
    hsCode: '8542.31.00',
    packagesCount: 120,
    packageType: 'Kiện carton chống sốc & chống tĩnh điện (ESD Cartons)',
    grossWeightKg: 14200,
    cbm: 54.0,
    freightTerms: 'PREPAID',
    blType: 'SEA_WAYBILL',
    containerCount: 1,
    containersCount: 1,
    status: 'PENDING_PAYMENT',
    eta: '02/11/2026 08:00',
    charges: [
      { id: 'ch-8', chargeName: 'Phí THC Cảng đến', amount: 3800000, currency: 'VND', isPaid: false },
      { id: 'ch-9', chargeName: 'Phí phát hành D/O', amount: 880000, currency: 'VND', isPaid: false },
    ],
    containers: [
      {
        id: 'cnt-5',
        containerNumber: 'EMCU7710924',
        containerType: '40HC',
        sealNumber: 'EMC-99120',
        weightKg: 14200,
        tareWeightKg: 3850,
        cargoType: 'Linh kiện vi mạch điện tử',
        status: 'DISCHARGED',
        dischargedAt: '02/11/2026 09:30',
      },
    ],
  },
  {
    id: 'bl-004',
    blNumber: 'CMA2026SIN9901',
    voyageNumber: 'EG-2605W',
    vesselName: 'EVER GLORY',
    consignee: 'Công ty TNHH Nhựa Duy Tân',
    consigneeName: 'Công ty TNHH Nhựa Duy Tân',
    shipper: 'ExxonMobil Chemical Asia Pacific (Jurong, Singapore)',
    notifyParty: 'Duy Tân Plastics Industrial Complex (KCN Tân Đô, Long An)',
    pol: 'Singapore Port (SGSIN)',
    pod: 'Cát Lái, Hồ Chí Minh (VNCLI)',
    cargoDescription: 'Hạt nhựa nguyên sinh Polyethylene LLDPE dạng viên (Virgin LLDPE Pellets)',
    hsCode: '3901.10.12',
    packagesCount: 1200,
    packageType: 'Bao PE 25kg quấn màng co (25kg bags shrink wrapped)',
    grossWeightKg: 52000,
    cbm: 75.0,
    freightTerms: 'PREPAID',
    blType: 'ORIGINAL',
    containerCount: 2,
    containersCount: 2,
    status: 'RELEASED',
    eta: '02/11/2026 08:00',
    charges: [
      { id: 'ch-10', chargeName: 'Phí THC Cảng đến', amount: 7600000, currency: 'VND', isPaid: true, paidAt: '02/11/2026 08:30' },
      { id: 'ch-11', chargeName: 'Phí D/O', amount: 880000, currency: 'VND', isPaid: true, paidAt: '02/11/2026 08:30' },
    ],
    containers: [
      {
        id: 'cnt-6',
        containerNumber: 'CMAU3109281',
        containerType: '40GP',
        sealNumber: 'CM-00192',
        weightKg: 26000,
        tareWeightKg: 3600,
        cargoType: 'Hạt nhựa công nghiệp',
        status: 'GATE_OUT',
        dischargedAt: '02/11/2026 08:45',
        gateOutAt: '03/11/2026 09:15',
      },
      {
        id: 'cnt-7',
        containerNumber: 'CMAU3109297',
        containerType: '40GP',
        sealNumber: 'CM-00193',
        weightKg: 26000,
        tareWeightKg: 3600,
        cargoType: 'Hạt nhựa công nghiệp',
        status: 'GATE_OUT',
        dischargedAt: '02/11/2026 08:55',
        gateOutAt: '03/11/2026 09:30',
      },
    ],
  },
];

let deliveryOrders = [
  {
    id: 'do-001',
    doNumber: 'DO-2026-8819',
    blNumber: 'ONE2026SGN7712',
    consignee: 'Công ty Cổ phần Sữa Việt Nam (Vinamilk)',
    vesselName: 'EVER GLORY',
    voyageNumber: 'EG-2605W',
    validUntil: '08/11/2026 23:59',
    status: 'ACTIVE',
    containerCount: 2,
    emptyReturnDepot: 'Depot Tân Cảng Suối Tiên',
    freeDetDays: 7,
    containers: [
      { containerNumber: 'ONEY4120934', containerType: '40HC', sealNumber: 'ONE-88123', weightKg: 21000 },
      { containerNumber: 'ONEY4120955', containerType: '40HC', sealNumber: 'ONE-88124', weightKg: 21000 },
    ],
    issuedAt: '02/11/2026 11:30',
  },
  {
    id: 'do-002',
    doNumber: 'DO-2026-8805',
    blNumber: 'CMA2026SIN9901',
    consignee: 'Công ty TNHH Nhựa Duy Tân',
    vesselName: 'EVER GLORY',
    voyageNumber: 'EG-2605W',
    validUntil: '06/11/2026 23:59',
    status: 'ACTIVE',
    containerCount: 2,
    emptyReturnDepot: 'Depot ICD Phước Long 1',
    freeDetDays: 5,
    containers: [
      { containerNumber: 'CMAU3109281', containerType: '40GP', sealNumber: 'CM-00192', weightKg: 26000 },
      { containerNumber: 'CMAU3109297', containerType: '40GP', sealNumber: 'CM-00193', weightKg: 26000 },
    ],
    issuedAt: '02/11/2026 09:00',
  },
];

let containersMaster = [
  {
    containerNumber: 'MSKU9284710',
    containerType: '20GP',
    status: 'LƯU BÃI CẢNG (DISCHARGED)',
    currentLocation: 'Bãi Cát Lái - Block D3, Bay 14, Row 02, Tier 3',
    sealNumber: 'MSK-771239',
    vesselVoyage: 'EVER GLORY / EG-2605W',
    vesselName: 'EVER GLORY',
    weightKg: 29200,
    bookingOrBLNumber: 'MSK2026VN009182',
    events: [
      { id: 'ev1', timestamp: '02/11/2026 10:15', eventCode: 'DISCH', title: 'Dỡ container khỏi tàu', location: 'Cầu bến B1 Cát Lái', details: 'Dỡ an toàn bởi cẩu STS số 02' },
      { id: 'ev2', timestamp: '02/11/2026 10:45', eventCode: 'YARD_IN', title: 'Hạ bãi container lưu giữ', location: 'Block D3, Bãi Cát Lái' },
    ],
    demDet: {
      containerNumber: 'MSKU9284710',
      calculationDate: '03/11/2026 10:00',
      dischargedDate: '02/11/2026 10:15',
      gateOutDate: undefined,
      totalDays: 2,
      freeDays: 5,
      chargeableDays: 0,
      currency: 'VND',
      totalAmount: 0,
      tiers: [
        { tierName: 'Miễn phí tiêu chuẩn (Free time)', fromDay: 1, toDay: 5, ratePerDay: 0, daysCount: 2, subTotal: 0 },
        { tierName: 'Mức 1 (Ngày 6 - 10)', fromDay: 6, toDay: 10, ratePerDay: 150000, daysCount: 0, subTotal: 0 },
        { tierName: 'Mức 2 (Từ ngày 11 trở đi)', fromDay: 11, toDay: 99, ratePerDay: 300000, daysCount: 0, subTotal: 0 },
      ],
    },
  },
  {
    containerNumber: 'ONEY4120934',
    containerType: '40HC',
    status: 'ĐÃ CẤP EDO - CHỜ GATE-OUT',
    currentLocation: 'Bãi Cát Lái - Block B2, Bay 22, Row 04, Tier 2',
    sealNumber: 'ONE-88123',
    vesselVoyage: 'EVER GLORY / EG-2605W',
    vesselName: 'EVER GLORY',
    weightKg: 21000,
    bookingOrBLNumber: 'ONE2026SGN7712',
    events: [
      { id: 'ev3', timestamp: '02/11/2026 11:45', eventCode: 'DISCH', title: 'Dỡ từ tàu xuống bãi', location: 'Cầu B1' },
      { id: 'ev4', timestamp: '02/11/2026 11:30', eventCode: 'EDO_ISSUED', title: 'Hãng tàu cấp eDO', location: 'Cổng thông tin Hãng tàu' },
    ],
    demDet: {
      containerNumber: 'ONEY4120934',
      calculationDate: '03/11/2026 10:00',
      dischargedDate: '02/11/2026 11:45',
      gateOutDate: undefined,
      totalDays: 2,
      freeDays: 5,
      chargeableDays: 0,
      currency: 'VND',
      totalAmount: 0,
      tiers: [
        { tierName: 'Miễn phí tiêu chuẩn', fromDay: 1, toDay: 5, ratePerDay: 0, daysCount: 2, subTotal: 0 },
      ],
    },
  },
];

let portMessages = [
  { id: 'msg-1', messageType: 'COPRAR', direction: 'OUTBOUND', timestamp: '02/11/2026 06:00', status: 'SUCCESS', referenceNumber: 'COPRAR-EG2605W-DISCH', payloadSummary: 'Lệnh dỡ hàng 148 containers từ tàu EVER GLORY' },
  { id: 'msg-2', messageType: 'COARRI', direction: 'INBOUND', timestamp: '02/11/2026 12:30', status: 'SUCCESS', referenceNumber: 'COARRI-EG2605W-FINAL', payloadSummary: 'Xác nhận cảng đã dỡ hoàn tất 148 containers vào bãi' },
  { id: 'msg-3', messageType: 'CODECO_OUT', direction: 'INBOUND', timestamp: '03/11/2026 09:30', status: 'SUCCESS', referenceNumber: 'GATEOUT-CMAU3109281', payloadSummary: 'Báo cáo container CMAU3109281 Gate Out khỏi Cổng E' },
  { id: 'msg-4', messageType: 'VERMAS', direction: 'OUTBOUND', timestamp: '03/11/2026 08:00', status: 'SUCCESS', referenceNumber: 'VERMAS-BKG0890', payloadSummary: 'Gửi khai báo VGM 6 containers xuất khẩu sang hệ thống Cảng' },
];

// ── Exported Service Methods ─────────────────────────────────────────────────

const shippingService = {
  // ── Bookings ────────────────────────────
  getBookings: async (params = {}) => {
    let filtered = [...bookings];
    if (params.search) {
      const q = params.search.toLowerCase();
      filtered = filtered.filter(
        (b) =>
          b.bookingNumber.toLowerCase().includes(q) ||
          b.shipperName.toLowerCase().includes(q) ||
          b.destinationPort.toLowerCase().includes(q)
      );
    }
    if (params.status) {
      filtered = filtered.filter((b) => b.status === params.status);
    }
    if (params.voyage) {
      filtered = filtered.filter((b) => b.voyageNumber === params.voyage);
    }

    const page = parseInt(params.page, 10) || 1;
    const pageSize = parseInt(params.pageSize, 10) || 10;
    const totalItems = filtered.length;
    const totalPages = Math.ceil(totalItems / pageSize) || 1;
    const start = (page - 1) * pageSize;
    const items = filtered.slice(start, start + pageSize);

    return {
      success: true,
      data: items,
      items,
      total: totalItems,
      totalCount: totalItems,
      page,
      pageSize,
      totalPages,
    };
  },

  getBookingById: async (id) => {
    const booking = bookings.find((b) => b.id === id || b.bookingNumber === id);
    if (!booking) return null;
    return booking;
  },

  approveBooking: async (id, data = {}) => {
    const booking = bookings.find((b) => b.id === id || b.bookingNumber === id);
    if (!booking) throw new Error('Không tìm thấy booking yêu cầu.');

    booking.status = 'APPROVED';
    booking.voyageNumber = data.voyageNumber || booking.voyageNumber;
    booking.depotPickUp = data.depotPickUp || 'Depot Tân Cảng Suối Tiên';
    booking.freeDemDays = data.freeDemDays || 7;
    booking.freeDetDays = data.freeDetDays || 5;

    booking.history = booking.history || [];
    booking.history.unshift({
      id: `h-${Date.now()}`,
      timestamp: new Date().toLocaleString('vi-VN'),
      actor: 'Hãng tàu NexusPort',
      action: 'DUYỆT_BOOKING',
      description: `Đã duyệt chỗ, chỉ định lấy vỏ tại ${booking.depotPickUp}, miễn phí DET ${booking.freeDetDays} ngày`,
    });

    return booking;
  },

  rejectBooking: async (id, data = {}) => {
    const booking = bookings.find((b) => b.id === id || b.bookingNumber === id);
    if (!booking) throw new Error('Không tìm thấy booking yêu cầu.');

    booking.status = 'REJECTED';
    booking.rejectionReason = data.reason || 'Tàu đã hết chỗ hoặc hạn mức tải trọng';

    booking.history = booking.history || [];
    booking.history.unshift({
      id: `h-${Date.now()}`,
      timestamp: new Date().toLocaleString('vi-VN'),
      actor: 'Hãng tàu NexusPort',
      action: 'TỪ_CHỐI_BOOKING',
      description: `Từ chối cấp chỗ: ${booking.rejectionReason}`,
    });

    return booking;
  },

  issueReleaseOrder: async (id, data = {}) => {
    const booking = bookings.find((b) => b.id === id || b.bookingNumber === id);
    if (!booking) throw new Error('Không tìm thấy booking yêu cầu.');

    booking.depotPickUp = data.depotPickUp || booking.depotPickUp || 'Depot ICD Phước Long 3';
    booking.history = booking.history || [];
    booking.history.unshift({
      id: `h-${Date.now()}`,
      timestamp: new Date().toLocaleString('vi-VN'),
      actor: 'Hãng tàu NexusPort',
      action: 'CẤP_LỆNH_LẤY_VỎ',
      description: `Phát hành Lệnh cấp vỏ rỗng (RO) tại ${booking.depotPickUp}`,
    });

    return { success: true, booking };
  },

  // ── Import Cargo & Manifest ─────────────
  getVoyages: async () => {
    return {
      success: true,
      data: voyages,
      items: voyages,
      total: voyages.length,
    };
  },

  getManifestByVoyage: async (voyageNumber) => {
    const bls = blList.filter((b) => b.voyageNumber.toLowerCase() === (voyageNumber || '').toLowerCase());
    return {
      success: true,
      voyageNumber,
      totalBL: bls.length,
      blList: bls,
      data: bls,
    };
  },

  getBLByNumber: async (blNumber) => {
    const bl = blList.find((b) => b.blNumber.toLowerCase() === (blNumber || '').toLowerCase());
    if (!bl) return null;
    return bl;
  },

  issueDeliveryOrderForBL: async (blNumber, data = {}) => {
    const bl = blList.find((b) => b.blNumber.toLowerCase() === (blNumber || '').toLowerCase());
    if (!bl) throw new Error('Không tìm thấy vận đơn B/L.');

    // Kiểm tra xem phí đã thanh toán chưa
    const unpaidCharges = (bl.charges || []).filter((c) => !c.isPaid);
    if (unpaidCharges.length > 0) {
      throw new Error(`Chưa thể cấp D/O do còn ${unpaidCharges.length} khoản phí chưa thanh toán.`);
    }

    const doNumber = `DO-${new Date().getFullYear()}-${Math.floor(1000 + Math.random() * 9000)}`;
    const newDO = {
      id: `do-${Date.now()}`,
      doNumber,
      blNumber: bl.blNumber,
      consignee: bl.consignee,
      vesselName: bl.vesselName,
      voyageNumber: bl.voyageNumber,
      validUntil: data.validUntil || '08/11/2026 23:59',
      status: 'ACTIVE',
      containerCount: bl.containerCount || bl.containers?.length || 1,
      emptyReturnDepot: data.emptyReturnDepot || 'Depot Tân Cảng Suối Tiên',
      freeDetDays: data.freeDetDays || 7,
      containers: (bl.containers || []).map((c) => ({
        containerNumber: c.containerNumber,
        containerType: c.containerType,
        sealNumber: c.sealNumber,
        weightKg: c.weightKg,
      })),
      issuedAt: new Date().toLocaleString('vi-VN'),
    };

    deliveryOrders.unshift(newDO);
    bl.status = 'DO_ISSUED';

    return { success: true, doNumber, deliveryOrder: newDO };
  },

  // ── Delivery Orders ─────────────────────
  getDeliveryOrders: async (params = {}) => {
    let filtered = [...deliveryOrders];
    if (params.search) {
      const q = params.search.toLowerCase();
      filtered = filtered.filter(
        (d) =>
          d.doNumber.toLowerCase().includes(q) ||
          d.blNumber.toLowerCase().includes(q) ||
          d.consignee.toLowerCase().includes(q)
      );
    }
    if (params.status) {
      filtered = filtered.filter((d) => d.status === params.status);
    }

    const page = parseInt(params.page, 10) || 1;
    const pageSize = parseInt(params.pageSize, 10) || 10;
    const totalItems = filtered.length;
    const totalPages = Math.ceil(totalItems / pageSize) || 1;
    const start = (page - 1) * pageSize;
    const items = filtered.slice(start, start + pageSize);

    return {
      success: true,
      data: items,
      items,
      total: totalItems,
      page,
      pageSize,
      totalPages,
    };
  },

  extendDeliveryOrder: async (id, newValidUntil) => {
    const doItem = deliveryOrders.find((d) => d.id === id || d.doNumber === id);
    if (!doItem) throw new Error('Không tìm thấy Lệnh giao hàng.');

    doItem.validUntil = newValidUntil;
    doItem.status = 'ACTIVE';
    doItem.updatedAt = new Date().toLocaleString('vi-VN');
    return doItem;
  },

  holdDeliveryOrder: async (id, reason) => {
    const doItem = deliveryOrders.find((d) => d.id === id || d.doNumber === id);
    if (!doItem) throw new Error('Không tìm thấy Lệnh giao hàng.');

    doItem.status = 'HOLD';
    doItem.holdReason = reason || 'Giữ hàng theo yêu cầu hoặc kiểm soát công nợ';
    doItem.updatedAt = new Date().toLocaleString('vi-VN');
    return doItem;
  },

  releaseDeliveryOrder: async (id) => {
    const doItem = deliveryOrders.find((d) => d.id === id || d.doNumber === id);
    if (!doItem) throw new Error('Không tìm thấy Lệnh giao hàng.');

    doItem.status = 'ACTIVE';
    doItem.holdReason = undefined;
    doItem.updatedAt = new Date().toLocaleString('vi-VN');
    return doItem;
  },

  cancelDeliveryOrder: async (id, reason) => {
    const doItem = deliveryOrders.find((d) => d.id === id || d.doNumber === id);
    if (!doItem) throw new Error('Không tìm thấy Lệnh giao hàng.');

    doItem.status = 'CANCELLED';
    doItem.holdReason = reason || 'Đã hủy lệnh D/O';
    doItem.updatedAt = new Date().toLocaleString('vi-VN');
    return doItem;
  },

  // ── Containers & Depots ─────────────────
  getContainers: async (params = {}) => {
    let filtered = [...containersMaster];
    if (params.search) {
      const q = params.search.toLowerCase();
      filtered = filtered.filter(
        (c) =>
          c.containerNumber.toLowerCase().includes(q) ||
          c.bookingOrBLNumber.toLowerCase().includes(q) ||
          c.currentLocation.toLowerCase().includes(q)
      );
    }
    return {
      success: true,
      data: filtered,
      items: filtered,
      total: filtered.length,
    };
  },

  getContainerByNumber: async (containerNumber) => {
    const cnt = containersMaster.find(
      (c) => c.containerNumber.toUpperCase() === (containerNumber || '').toUpperCase()
    );
    if (!cnt) return null;
    return cnt;
  },

  // ── EDI / Port Sync ─────────────────────
  getPortMessages: async () => {
    return {
      success: true,
      data: portMessages,
      items: portMessages,
      total: portMessages.length,
    };
  },

  retryPortMessage: async (id) => {
    const msg = portMessages.find((m) => m.id === id);
    if (msg) {
      msg.status = 'SUCCESS';
      msg.timestamp = new Date().toLocaleString('vi-VN');
    }
    return { success: true, message: msg };
  },

  // ── Stats / Overview ────────────────────
  getStats: async () => {
    return {
      success: true,
      data: {
        totalBookings: bookings.length,
        pendingBookings: bookings.filter((b) => b.status === 'PENDING').length,
        approvedBookings: bookings.filter((b) => b.status === 'APPROVED').length,
        blIssuedBookings: bookings.filter((b) => b.status === 'BL_ISSUED').length,
        totalVoyages: voyages.length,
        totalBLs: blList.length,
        pendingPaymentBLs: blList.filter((b) => b.status === 'PENDING_PAYMENT').length,
        pendingDOBLs: blList.filter((b) => b.status === 'PENDING_DO').length,
        activeDOs: deliveryOrders.filter((d) => d.status === 'ACTIVE').length,
        heldDOs: deliveryOrders.filter((d) => d.status === 'HOLD').length,
        totalContainersDischarged: containersMaster.length,
      },
    };
  },
};

module.exports = shippingService;


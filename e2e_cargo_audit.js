'use strict';

/**
 * COMPREHENSIVE END-TO-END AUDIT & TEST SUITE FOR NGƯỜI 1
 * Mô-đun: Thông báo và Theo dõi Chứng từ cho Chủ hàng & Công ty Vận tải
 * Phiên bản: 14 Xuất khẩu + 14 Nhập khẩu + 15 Nghiệp vụ chung
 */

const { sequelize } = require('./src/config/database');
const cargoNotificationService = require('./src/modules/cargo-notifications/cargoNotification.service');
const { generateToken } = require('./src/utils/jwt');

const API_BASE = 'http://localhost:3001/api/cargo-notifications';

async function runFullAudit() {
  console.log('\n================================================================');
  console.log('   BẮT ĐẦU AUDIT & KIỂM THỬ TOÀN DIỆN MÔ-ĐUN NGƯỜI 1');
  console.log('   Thời gian:', new Date().toLocaleString('vi-VN'));
  console.log('================================================================\n');

  const testReport = {
    total: 0,
    passed: 0,
    failed: 0,
    details: [],
  };

  function assertCheck(category, name, condition, note = '') {
    testReport.total++;
    const isPass = !!condition;
    if (isPass) {
      testReport.passed++;
      console.log(`  [PASS] [${category}] ${name} ${note ? `(${note})` : ''}`);
    } else {
      testReport.failed++;
      console.error(`  [FAIL] [${category}] ${name} ${note ? `(${note})` : ''}`);
    }
    testReport.details.push({ category, name, passed: isPass, note });
  }

  // 0. Tạo Token Xác thực qua Login API
  const loginRes = await fetch('http://localhost:3001/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: 'carrier01', password: '123456' }),
  }).then(r => r.json());
  const authToken = loginRes?.data?.token;
  const headers = {
    'Content-Type': 'application/json',
    'Authorization': 'Bearer ' + authToken,
  };

  const ts = Date.now();
  const testBooking = `BKG-AUDIT-${ts}`;
  const testBL = `BL-AUDIT-${ts}`;
  const testEDO = `EDO-AUDIT-${ts}`;
  const testOrder = `ORD-AUDIT-${ts}`;
  const testEIR = `EIR-AUDIT-${ts}`;
  const testConsigneeMST = `0401${Math.floor(100000 + Math.random() * 900000)}`;

  // -------------------------------------------------------------
  // PHẦN 1: KIỂM THỬ 14 NGHIỆP VỤ XUẤT KHẨU (EXPORT X1 -> X14)
  // -------------------------------------------------------------
  console.log('\n--- [PHẦN 1: KIỂM THỬ 14 NGHIỆP VỤ XUẤT KHẨU] ---');

  // X1: BookingConfirmed
  const resX1 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x1-${ts}`,
    eventCode: 'BookingConfirmed',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      vesselName: 'EVER GLORY',
      voyageNo: '2601N',
      etd: new Date(Date.now() + 72 * 3600 * 1000).toISOString(),
      cyCutOff: new Date(Date.now() + 48 * 3600 * 1000).toISOString(),
      vgmCutOff: new Date(Date.now() + 44 * 3600 * 1000).toISOString(),
      siCutOff: new Date(Date.now() + 40 * 3600 * 1000).toISOString(),
      emptyPickupPlace: 'Depot Tân Cảng Đà Nẵng',
      shipperTaxCode: '0401889988',
      shipper: 'Công ty Cổ phần Thép Hòa Phát',
    },
  });
  assertCheck('EXPORT', 'X1: BookingConfirmed - Đầy đủ hạn CY/VGM/SI & Action Mở booking',
    resX1.success && resX1.notification.action_label === 'Mở booking' && resX1.notification.document_type === 'BOOKING');

  // Kiểm tra Reminder tự động sinh cho VGM và CY
  const [remindersX1] = await sequelize.query(`
    SELECT count(*)::int as cnt FROM cargo_reminders 
    WHERE target_id = :bookingNo AND target_type IN ('VGM_CUTOFF', 'CY_CUTOFF')
  `, { replacements: { bookingNo: testBooking }, type: sequelize.QueryTypes.SELECT });
  assertCheck('REMINDER', 'Tự động tạo 4 mốc reminder (T-24H và T-2H cho VGM & CY)', remindersX1.cnt >= 2, `Số reminders: ${remindersX1.cnt}`);

  // X2: BookingRejected
  const resX2 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x2-${ts}`,
    eventCode: 'BookingRejected',
    direction: 'export',
    payload: {
      bookingNo: `${testBooking}-REJ`,
      reason: 'Tàu đã đầy tải trọng (Vessel Overbooked)',
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X2: BookingRejected - Mức Khẩn & Action Tạo booking mới',
    resX2.success && resX2.notification.priority === 'urgent' && resX2.notification.action_label === 'Tạo booking mới');

  // X3: BookingUpdated / ScheduleChanged
  const resX3 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x3-${ts}`,
    eventCode: 'BookingUpdated',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      changes: [
        { field: 'ETD', oldValue: '2026-11-20T10:00:00Z', newValue: '2026-11-21T06:00:00Z' },
        { field: 'CY Cut-off', oldValue: '2026-11-19T18:00:00Z', newValue: '2026-11-20T12:00:00Z' },
      ],
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X3: BookingUpdated - Hiển thị giá trị cũ/mới & Action Xem chi tiết',
    resX3.success && resX3.notification.message.includes('giá trị cũ') && resX3.notification.action_label === 'Xem chi tiết');

  // X4: ContainerListAnnounced (Thành công không lỗi)
  const resX4a = await cargoNotificationService.ingestEvent({
    eventId: `evt-x4a-${ts}`,
    eventCode: 'ContainerListAnnounced',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      containers: ['MSKU9082134', 'TCKU4829102'],
      errors: [],
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X4a: ContainerListAnnounced (Hợp lệ) - Ghi nhận cont & Nhắc khai VGM',
    resX4a.success && resX4a.notification.priority === 'normal' && resX4a.notification.action_label === 'Khai hoặc nộp');

  // X5: ContainerListAnnounced (Có lỗi từng dòng)
  const resX4b = await cargoNotificationService.ingestEvent({
    eventId: `evt-x4b-${ts}`,
    eventCode: 'ContainerListAnnounced',
    direction: 'export',
    payload: {
      bookingNo: `${testBooking}-ERR`,
      containers: ['MSKU9082134', 'TCKU4829102'],
      errors: [
        'Dòng 1: Container MSKU9082134 thiếu phiếu xác nhận VGM',
        'Dòng 2: Container TCKU4829102 sai cấu trúc số niêm chì',
      ],
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X4b: ContainerListAnnounced (Có lỗi) - Mức Khẩn & Action Sửa danh sách',
    resX4b.success && resX4b.notification.priority === 'urgent' && resX4b.notification.action_label === 'Sửa danh sách');

  // X6: Nhắc hạn CutoffApproaching / DeadlineApproaching
  const resX6 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x6-${ts}`,
    eventCode: 'CutoffApproaching',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      cutoffType: 'VGM & CY Cut-off',
      dueAt: new Date(Date.now() + 1.5 * 3600 * 1000).toISOString(), // Còn 1.5h -> Tự nâng mức Khẩn
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X6: CutoffApproaching < 2h - Tự động nâng mức Khẩn cấp',
    resX6.success && resX6.notification.priority === 'urgent' && resX6.notification.action_label === 'Khai hoặc nộp');

  // X7: Đặt dịch vụ hạ bãi (ServiceOrderSubmitted)
  const resX7 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x7-${ts}`,
    eventCode: 'ServiceOrderSubmitted',
    direction: 'export',
    payload: {
      orderCode: `${testOrder}-HA`,
      serviceType: 'Dịch vụ hạ bãi container xuất khẩu',
      amount: 480000,
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X7: ServiceOrderSubmitted - Thông báo lệnh chờ thanh toán',
    resX7.success && resX7.notification.action_type === 'pay_order');

  // X8: Container qua cổng vào cảng (ContainerGatedIn)
  const resX8 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x8-${ts}`,
    eventCode: 'ContainerGatedIn',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      containers: ['MSKU9082134', 'TCKU4829102'],
      eirNo: testEIR,
      truckPlate: '43C-99881',
      gateCode: 'GATE-01',
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X8: ContainerGatedIn - Gộp nhiều container, Action Xem EIR',
    resX8.success && resX8.notification.action_label === 'Xem EIR' && resX8.notification.document_type === 'EIR');

  // Kiểm tra Reminder CY Cut-off đã tự động HỦY sau khi Gate-In
  const [remindersCY] = await sequelize.query(`
    SELECT status FROM cargo_reminders 
    WHERE target_id = :bookingNo AND target_type = 'CY_CUTOFF' LIMIT 1
  `, { replacements: { bookingNo: testBooking }, type: sequelize.QueryTypes.SELECT });
  assertCheck('REMINDER', 'Tự động HỦY reminder CY_CUTOFF khi xe đã Gate-In',
    remindersCY && remindersCY.status === 'cancelled');

  // X9: Container bị từ chối ở cổng (ContainerGateRejected)
  const resX9 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x9-${ts}`,
    eventCode: 'ContainerGateRejected',
    direction: 'export',
    payload: {
      containerNo: 'MSKU9999999',
      reason: 'Quá hạn CY cut-off và sai niêm chì so với manifest',
      truckPlate: '43C-11223',
      gateCode: 'GATE-02',
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X9: ContainerGateRejected - Mức Khẩn & Action Liên hệ hãng tàu',
    resX9.success && resX9.notification.priority === 'urgent' && resX9.notification.action_label.includes('Liên hệ'));

  // X10: Chốt danh sách xếp tàu / Rớt tàu (ContainerRolledOver / VoyageListClosed)
  const resX10 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x10-${ts}`,
    eventCode: 'ContainerRolledOver',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      containers: ['TCKU4829102'],
      reason: 'Vượt trọng tải an toàn của tàu, dời sang chuyến sau',
      newVoyageNo: '2602N',
      newEtd: new Date(Date.now() + 96 * 3600 * 1000).toISOString(),
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X10: ContainerRolledOver (Rớt tàu) - Cảnh báo khẩn & Hiện chuyến mới',
    resX10.success && resX10.notification.priority === 'urgent' && resX10.notification.message.includes('2602N'));

  // X11: Container xếp lên tàu (ContainerLoaded)
  const resX11 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x11-${ts}`,
    eventCode: 'ContainerLoaded',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      containerNo: 'MSKU9082134',
      vesselName: 'EVER GLORY',
      voyageNo: '2601N',
      loadedAt: new Date().toISOString(),
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X11: ContainerLoaded - Thông báo bốc container an toàn lên tàu',
    resX11.success && resX11.notification.title.includes('đã xếp lên tàu'));

  // X12: Tàu rời cầu (VesselDeparted)
  const resX12 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x12-${ts}`,
    eventCode: 'VesselDeparted',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      vesselName: 'EVER GLORY',
      voyageNo: '2601N',
      departedAt: new Date().toISOString(),
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X12: VesselDeparted - Thông báo tàu chính thức khởi hành',
    resX12.success && resX12.notification.title.includes('ĐÃ RỜI CẦU'));

  // X13: Draft B/L cần xác nhận (DraftBLIssued)
  const resX13 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x13-${ts}`,
    eventCode: 'DraftBLIssued',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      blNumber: testBL,
      confirmationDeadline: new Date(Date.now() + 24 * 3600 * 1000).toISOString(),
      shipperTaxCode: '0401889988',
      shipper: 'Công ty Cổ phần Thép Hòa Phát',
    },
  });
  assertCheck('EXPORT', 'X13: DraftBLIssued - Action Xem và xác nhận draft, lưu DB DRAFT',
    resX13.success && resX13.notification.action_label === 'Xem và xác nhận draft');

  // Kiểm tra lưu DB bills_of_lading trạng thái DRAFT
  const [blDraft] = await sequelize.query(`
    SELECT status, draft_version FROM bills_of_lading WHERE bl_number = :blNumber
  `, { replacements: { blNumber: testBL }, type: sequelize.QueryTypes.SELECT });
  assertCheck('DATABASE', 'Bảng bills_of_lading đã lưu bản ghi DRAFT trong PostgreSQL',
    blDraft && blDraft.status === 'DRAFT');

  // X14: B/L chính thức phát hành (BLIssued)
  const resX14 = await cargoNotificationService.ingestEvent({
    eventId: `evt-x14-${ts}`,
    eventCode: 'BLIssued',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      blNumber: testBL,
      shippedOnBoardDate: new Date().toISOString(),
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('EXPORT', 'X14: BLIssued - Action Xem B/L, cập nhật DB ISSUED',
    resX14.success && resX14.notification.action_label === 'Xem B/L');

  // Kiểm tra cập nhật DB bills_of_lading sang ISSUED
  const [blIssued] = await sequelize.query(`
    SELECT status FROM bills_of_lading WHERE bl_number = :blNumber
  `, { replacements: { blNumber: testBL }, type: sequelize.QueryTypes.SELECT });
  assertCheck('DATABASE', 'Bảng bills_of_lading tự động chuyển trạng thái sang ISSUED',
    blIssued && blIssued.status === 'ISSUED');

  // -------------------------------------------------------------
  // PHẦN 2: KIỂM THỬ 14 NGHIỆP VỤ NHẬP KHẨU (IMPORT M1 -> M14)
  // -------------------------------------------------------------
  console.log('\n--- [PHẦN 2: KIỂM THỬ 14 NGHIỆP VỤ NHẬP KHẨU] ---');

  // M1: ManifestSent & Hàng đợi ngoại lệ khi Consignee chưa có tài khoản
  const resM1 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m1-${ts}`,
    eventCode: 'ManifestSent',
    direction: 'import',
    payload: {
      manifestId: `MNF-IN-${ts}`,
      blNumber: `${testBL}-IMP`,
      vesselName: 'CMA CGM CHENNAI',
      voyageNo: '2604S',
      eta: new Date(Date.now() + 48 * 3600 * 1000).toISOString(),
      containers: ['CMAU7788991', 'CMAU7788992'],
      consigneeName: 'Công ty Chưa Đăng Ký Test',
      consigneeTaxCode: testConsigneeMST,
    },
  });
  assertCheck('IMPORT', 'M1: ManifestSent - Nhận thông báo hàng đến',
    resM1.success && resM1.notification.action_label === 'Xem thông báo hàng đến');

  // Kiểm tra đưa vào unclaimed_consignee_queue
  const [unclaimed] = await sequelize.query(`
    SELECT status FROM unclaimed_consignee_queue WHERE consignee_tax_code = :taxCode LIMIT 1
  `, { replacements: { taxCode: testConsigneeMST }, type: sequelize.QueryTypes.SELECT });
  assertCheck('EXCEPTION', 'Consignee lạ tự động đưa vào Hàng đợi ngoại lệ (PENDING_CLAIM)',
    unclaimed && unclaimed.status === 'PENDING_CLAIM');

  // M2: ContainerDischarged (Dỡ hàng khỏi tàu)
  const resM2 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m2-${ts}`,
    eventCode: 'ContainerDischarged',
    direction: 'import',
    payload: {
      blNumber: `${testBL}-IMP`,
      containerNo: 'CMAU7788991',
      dischargedAt: new Date().toISOString(),
      yardLocation: 'BLOCK-B3-ROW02',
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M2: ContainerDischarged - Bắt đầu tính lưu bãi, vị trí bãi',
    resM2.success && resM2.notification.action_label === 'Xem container');

  // M3: e-D/O được cấp (EDoIssued)
  const resM3 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m3-${ts}`,
    eventCode: 'EDoIssued',
    direction: 'import',
    payload: {
      doNumber: testEDO,
      blNumber: `${testBL}-IMP`,
      validUntil: new Date(Date.now() + 72 * 3600 * 1000).toISOString(),
      emptyReturnDepot: 'Depot Tân Cảng Đà Nẵng',
      consigneeTaxCode: '0401889988',
      consigneeName: 'Công ty Cổ phần Thép Hòa Phát',
      containers: [{ containerNo: 'CMAU7788991', type: '40HC' }],
    },
  });
  assertCheck('IMPORT', 'M3: EDoIssued - Action Đặt lệnh lấy hàng & lưu DB RELEASED',
    resM3.success && resM3.notification.action_label === 'Đặt lệnh lấy hàng');

  // Kiểm tra bảng delivery_orders trong PostgreSQL
  const [edoInDB] = await sequelize.query(`
    SELECT status, empty_return_depot FROM delivery_orders WHERE do_number = :doNumber
  `, { replacements: { doNumber: testEDO }, type: sequelize.QueryTypes.SELECT });
  assertCheck('DATABASE', 'Bảng delivery_orders đã lưu bản ghi RELEASED trong PostgreSQL',
    edoInDB && edoInDB.status === 'RELEASED');

  // M4: e-D/O bị tạm giữ (EDoHeld)
  const resM4 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m4-${ts}`,
    eventCode: 'EDoHeld',
    direction: 'import',
    payload: {
      doNumber: testEDO,
      reason: 'Yêu cầu đối soát phụ phí hãng tàu',
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M4: EDoHeld - Mức Khẩn cấp & cập nhật DB HELD',
    resM4.success && resM4.notification.priority === 'urgent');

  const [edoHeldDB] = await sequelize.query(`
    SELECT status FROM delivery_orders WHERE do_number = :doNumber
  `, { replacements: { doNumber: testEDO }, type: sequelize.QueryTypes.SELECT });
  assertCheck('DATABASE', 'Bảng delivery_orders tự động cập nhật sang HELD',
    edoHeldDB && edoHeldDB.status === 'HELD');

  // M5: e-D/O được nhả giữ (EDoReleased)
  const resM5 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m5-${ts}`,
    eventCode: 'EDoReleased',
    direction: 'import',
    payload: {
      doNumber: testEDO,
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M5: EDoReleased - Mở khóa giao hàng, cập nhật DB RELEASED',
    resM5.success && resM5.notification.action_label === 'Đặt lệnh lấy hàng');

  // M6: e-D/O bị hủy (EDoCancelled)
  const resM6 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m6-${ts}`,
    eventCode: 'EDoCancelled',
    direction: 'import',
    payload: {
      doNumber: `${testEDO}-CAN`,
      reason: 'Sai số vận đơn theo yêu cầu Hãng tàu',
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M6: EDoCancelled - Mức Khẩn & Action Liên hệ hãng tàu',
    resM6.success && resM6.notification.priority === 'urgent');

  // M7: Chủ hàng đặt lệnh lấy container (ServiceOrderSubmitted)
  const resM7 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m7-${ts}`,
    eventCode: 'ServiceOrderSubmitted',
    direction: 'import',
    payload: {
      orderCode: `${testOrder}-NHAP`,
      serviceType: 'Lệnh giao nhận container nhập bãi',
      amount: 450000,
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M7: ServiceOrderSubmitted (Nhập) - Thông báo chờ thanh toán',
    resM7.success && resM7.notification.action_type === 'pay_order');

  // M8: Lệnh dịch vụ được duyệt (ServiceOrderApproved)
  const resM8 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m8-${ts}`,
    eventCode: 'ServiceOrderApproved',
    direction: 'import',
    payload: {
      orderCode: `${testOrder}-NHAP`,
      serviceType: 'Lệnh giao nhận container nhập bãi',
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M8: ServiceOrderApproved - Thông báo lệnh đã duyệt, đủ điều kiện vào bãi',
    resM8.success && resM8.notification.action_label === 'Xem lệnh');

  // M9: Lệnh dịch vụ bị từ chối (ServiceOrderRejected)
  const resM9 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m9-${ts}`,
    eventCode: 'ServiceOrderRejected',
    direction: 'import',
    payload: {
      orderCode: `${testOrder}-NHAP-REJ`,
      reason: 'Xe chưa đăng kiểm an toàn cổng cảng',
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M9: ServiceOrderRejected - Mức Khẩn & Lý do từ chối',
    resM9.success && resM9.notification.priority === 'urgent');

  // M10: Lệnh dịch vụ đã thanh toán (ServiceOrderPaid)
  const resM10 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m10-${ts}`,
    eventCode: 'ServiceOrderPaid',
    direction: 'import',
    payload: {
      orderCode: `${testOrder}-NHAP`,
      amount: 450000,
      paymentRef: 'PAY-VCB-99881',
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M10: ServiceOrderPaid - Đã thanh toán, chuyển sang điều độ',
    resM10.success && resM10.notification.action_label === 'Xem lệnh');

  // M11: Lệnh dịch vụ hoàn thành (ServiceOrderCompleted)
  const resM11 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m11-${ts}`,
    eventCode: 'ServiceOrderCompleted',
    direction: 'import',
    payload: {
      orderCode: `${testOrder}-NHAP`,
      serviceName: 'Nâng hạ container lên xe',
      invoiceNo: 'INV-2026-991',
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M11: ServiceOrderCompleted - Đã thực thi xong & hóa đơn điện tử',
    resM11.success && resM11.notification.action_type === 'view_invoice');

  // M12: Free time sắp hết (FreeTimeApproaching)
  const resM12 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m12-${ts}`,
    eventCode: 'FreeTimeApproaching',
    direction: 'import',
    payload: {
      doNumber: testEDO,
      dueAt: new Date(Date.now() + 1.2 * 3600 * 1000).toISOString(), // Còn 1.2h -> Mức Khẩn
      estimatedDemurrage: 500000,
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M12: FreeTimeApproaching - Tự động mức Khẩn cấp khi < 2h & phí dự kiến',
    resM12.success && resM12.notification.priority === 'urgent' && resM12.notification.action_label === 'Đặt lệnh lấy hàng');

  // M13: Container ra cổng (ContainerGatedOut)
  const resM13 = await cargoNotificationService.ingestEvent({
    eventId: `evt-m13-${ts}`,
    eventCode: 'ContainerGatedOut',
    direction: 'import',
    payload: {
      doNumber: testEDO,
      containerNo: 'CMAU7788991',
      eirNo: `${testEIR}-OUT`,
      truckPlate: '43C-99881',
      emptyReturnDepot: 'Depot Tân Cảng Đà Nẵng',
      consigneeTaxCode: '0401889988',
    },
  });
  assertCheck('IMPORT', 'M13: ContainerGatedOut - Giờ ra, số EIR, kết thúc lưu bãi',
    resM13.success && resM13.notification.action_label === 'Nhắc trả vỏ');

  // M14: Kiểm tra Reminder Trả vỏ rỗng tự động tạo sau Gate-Out
  const [remindersEmpty] = await sequelize.query(`
    SELECT status, due_at FROM cargo_reminders 
    WHERE target_id = :targetId AND target_type = 'EMPTY_RETURN' LIMIT 1
  `, { replacements: { targetId: 'CMAU7788991' }, type: sequelize.QueryTypes.SELECT });
  assertCheck('IMPORT', 'M14: Tự động lập Reminder nhắc trả vỏ rỗng về Depot sau khi Gate-Out',
    remindersEmpty && remindersEmpty.status === 'pending');

  // -------------------------------------------------------------
  // PHẦN 3: KIỂM THỬ TÍNH NĂNG CHUNG & HTTP API ENDPOINTS
  // -------------------------------------------------------------
  console.log('\n--- [PHẦN 3: KIỂM THỬ TÍNH NĂNG CHUNG & HTTP API ENDPOINTS] ---');

  // C1: Chống trùng lặp (Dedup Key Idempotency)
  const dupRes = await cargoNotificationService.ingestEvent({
    eventId: `evt-x1-${ts}`,
    eventCode: 'BookingConfirmed',
    direction: 'export',
    payload: {
      bookingNo: testBooking,
      shipperTaxCode: '0401889988',
    },
  });
  assertCheck('COMMON', 'C1: Chống trùng lặp (Idempotency) - Bỏ qua sự kiện lặp lại',
    dupRes.skipped === true && dupRes.reason === 'DUPLICATE_EVENT');

  // C2: API GET /api/cargo-notifications (Lấy danh sách thông báo)
  const httpNotif = await fetch(`${API_BASE}?limit=10`, { headers }).then(r => r.json());
  assertCheck('API', 'C2: API GET /cargo-notifications trả về HTTP 200 có pagination',
    httpNotif.success && Array.isArray(httpNotif.data) && httpNotif.pagination.total > 0);

  // C3: API GET /api/cargo-notifications/unread-count
  const httpUnread = await fetch(`${API_BASE}/unread-count`, { headers }).then(r => r.json());
  assertCheck('API', 'C3: API GET /unread-count trả về số tin chưa đọc chính xác',
    httpUnread.success && typeof httpUnread.data.unreadCount === 'number');

  // C4: API GET /api/cargo-notifications/todo (To-Do List)
  const httpTodo = await fetch(`${API_BASE}/todo?type=all`, { headers }).then(r => r.json());
  assertCheck('API', 'C4: API GET /todo trả về danh sách việc cần làm đã phân loại xuất/nhập',
    httpTodo.success && Array.isArray(httpTodo.data) && httpTodo.data.length > 0);

  // C5: API GET /api/cargo-notifications/documents (Trung tâm chứng từ EDO & BL từ DB)
  const httpDocsEDO = await fetch(`${API_BASE}/documents?type=EDO`, { headers }).then(r => r.json());
  assertCheck('API', 'C5a: API GET /documents?type=EDO query trực tiếp PostgreSQL',
    httpDocsEDO.success && httpDocsEDO.data.some(d => d.documentType === 'EDO'));

  const httpDocsBL = await fetch(`${API_BASE}/documents?type=BL`, { headers }).then(r => r.json());
  assertCheck('API', 'C5b: API GET /documents?type=BL query trực tiếp PostgreSQL',
    httpDocsBL.success && httpDocsBL.data.some(d => d.documentType === 'BL'));

  // C6: API GET /api/cargo-notifications/documents/EDO/:id (Chi tiết e-D/O)
  const httpDocDetail = await fetch(`${API_BASE}/documents/EDO/${testEDO}`, { headers }).then(r => r.json());
  assertCheck('API', 'C6: API GET /documents/EDO/:id trả về đầy đủ QR & Action Recommendation',
    httpDocDetail.success && httpDocDetail.data.doNumber === testEDO && httpDocDetail.data.qrData);

  // C7: Đánh dấu đã đọc (PUT /api/cargo-notifications/:id/read)
  const firstNotifId = httpNotif.data[0].id;
  const readRes = await fetch(`${API_BASE}/${firstNotifId}/read`, { method: 'PUT', headers }).then(r => r.json());
  assertCheck('API', 'C7: API PUT /:id/read đánh dấu thông báo đã đọc',
    readRes.success && readRes.data.is_read === true);

  // C8: Xử lý claimConsignee trong Hàng đợi ngoại lệ
  const [claimItem] = await sequelize.query(`
    SELECT id FROM unclaimed_consignee_queue WHERE consignee_tax_code = :taxCode LIMIT 1
  `, { replacements: { taxCode: testConsigneeMST }, type: sequelize.QueryTypes.SELECT });
  if (claimItem) {
    const claimRes = await fetch(`${API_BASE}/unclaimed-consignees/${claimItem.id}/claim`, {
      method: 'POST',
      headers,
      body: JSON.stringify({ companyId: 'ca2f3007-30b5-4236-9013-95c66dcdf5b6' }),
    }).then(r => r.json());
    assertCheck('API', 'C8: API POST /unclaimed-consignees/:id/claim gán người nhận thành công',
      claimRes.success && claimRes.data.status === 'CLAIMED');
  }

  // -------------------------------------------------------------
  // TỔNG KẾT
  // -------------------------------------------------------------
  console.log('\n================================================================');
  console.log(`   KẾT QUẢ AUDIT & TESTING: ${testReport.passed}/${testReport.total} CHECKPOINTS PASSED`);
  if (testReport.failed === 0) {
    console.log('   TRẠNG THÁI: TOÀN BỘ CHỨC NĂNG HOẠT ĐỘNG HOÀN HẢO 100%!');
  } else {
    console.error(`   CẢNH BÁO: CÓ ${testReport.failed} CHECKPOINT THẤT BẠI!`);
  }
  console.log('================================================================\n');

  return testReport;
}

runFullAudit()
  .then(() => process.exit(0))
  .catch((err) => {
    console.error('Fatal Audit Error:', err);
    process.exit(1);
  });

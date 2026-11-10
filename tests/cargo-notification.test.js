'use strict';

const cargoNotificationService = require('../src/modules/cargo-notifications/cargoNotification.service');
const { sequelize } = require('../src/config/database');

describe('Người 1: Cargo Notification & Document Tracking Module Tests', () => {
  const testBookingNo = `TEST_BKG_${Date.now()}`;
  const testBlNumber = `TEST_BL_${Date.now()}`;
  const testEirNo = `TEST_EIR_${Date.now()}`;
  const testDoNumber = `TEST_EDO_${Date.now()}`;

  beforeAll(async () => {
    // Ensure DB connected
    await sequelize.authenticate();
  });

  afterAll(async () => {
    // Cleanup test notifications and reminders
    await sequelize.query(`
      DELETE FROM cargo_notifications WHERE dedup_key LIKE '%TEST_%';
      DELETE FROM cargo_reminders WHERE target_id LIKE '%TEST_%';
      DELETE FROM unclaimed_consignee_queue WHERE bl_number LIKE '%TEST_%';
    `);
  });

  describe('1. Khóa chống trùng (Deduplication / Idempotency)', () => {
    it('Sự kiện đầu tiên được tạo thành công, sự kiện thứ hai cùng khóa bị bỏ qua', async () => {
      const event = {
        eventId: `evt-dedup-1`,
        eventCode: 'BookingConfirmed',
        direction: 'export',
        payload: {
          bookingNo: testBookingNo,
          vesselName: 'EVER GLORY',
          voyageNo: '2601N',
          etd: new Date(Date.now() + 86400000).toISOString(),
          cyCutOff: new Date(Date.now() + 43200000).toISOString(),
          vgmCutOff: new Date(Date.now() + 36000000).toISOString(),
          siCutOff: new Date(Date.now() + 30000000).toISOString(),
          shipperTaxCode: '0401889988',
        },
      };

      const res1 = await cargoNotificationService.ingestEvent(event);
      expect(res1.success).toBe(true);
      expect(res1.notification).toBeDefined();
      expect(res1.notification.title).toContain(testBookingNo);

      // Gửi lại sự kiện trùng lặp
      const res2 = await cargoNotificationService.ingestEvent(event);
      expect(res2.skipped).toBe(true);
      expect(res2.reason).toBe('DUPLICATE_EVENT');
    });
  });

  describe('2. Phân loại mức ưu tiên (Priority Classification)', () => {
    it('Container bị từ chối ở cổng (ContainerGateRejected) phải có mức ưu tiên Khẩn (urgent)', async () => {
      const event = {
        eventId: `evt-gate-rej-1`,
        eventCode: 'ContainerGateRejected',
        direction: 'export',
        payload: {
          containerNo: 'TESTU1234567',
          bookingNo: testBookingNo,
          truckPlate: '43C-888.88',
          gateCode: 'GATE-01',
          reason: 'Quá hạn CY cut-off và chưa có xác nhận từ hãng tàu',
          shipperTaxCode: '0401889988',
        },
      };

      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.priority).toBe('urgent');
      expect(res.notification.title).toContain('KHẨN CẤP');
      expect(res.notification.action_type).toBe('contact_support');
    });

    it('e-D/O bị tạm giữ (EDoHeld) phải có mức ưu tiên Khẩn (urgent)', async () => {
      const event = {
        eventId: `evt-edo-held-1`,
        eventCode: 'EDoHeld',
        direction: 'import',
        payload: {
          doNumber: testDoNumber,
          blNumber: testBlNumber,
          reason: 'Nghi ngờ sai lệch thông tin hàng hóa',
          consigneeTaxCode: '0401889988',
        },
      };

      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.priority).toBe('urgent');
      expect(res.notification.title).toContain('BỊ TẠM GIỮ');
    });

    it('e-D/O được cấp bình thường (EDoIssued) có mức ưu tiên Thường (normal)', async () => {
      const event = {
        eventId: `evt-edo-issued-1`,
        eventCode: 'EDoIssued',
        direction: 'import',
        payload: {
          doNumber: `${testDoNumber}_NORM`,
          blNumber: testBlNumber,
          validUntil: new Date(Date.now() + 86400000 * 3).toISOString(),
          emptyReturnDepot: 'Depot Tân Cảng Đà Nẵng',
          consigneeTaxCode: '0401889988',
        },
      };

      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.priority).toBe('normal');
      expect(res.notification.action_type).toBe('order_service_pickup');
    });
  });

  describe('3. Tự động hóa Reminder (Tạo khi có lịch & Hủy khi hoàn thành)', () => {
    it('Khi xe qua cổng vào (ContainerGatedIn), reminder CY_CUTOFF tự động được HỦY', async () => {
      // Kiểm tra reminder CY_CUTOFF của testBookingNo đang pending
      const [remBefore] = await sequelize.query(`
        SELECT count(*)::int AS count FROM cargo_reminders
        WHERE target_type = 'CY_CUTOFF' AND target_id = :bookingNo AND status = 'pending'
      `, { replacements: { bookingNo: testBookingNo }, type: sequelize.QueryTypes.SELECT });
      expect(remBefore.count).toBeGreaterThan(0);

      // Phát sự kiện ContainerGatedIn
      const gateInEvent = {
        eventId: `evt-gate-in-1`,
        eventCode: 'ContainerGatedIn',
        direction: 'export',
        payload: {
          eirNo: testEirNo,
          containerNo: 'TESTU1234567',
          bookingNo: testBookingNo,
          truckPlate: '43C-888.88',
          gateCode: 'GATE-01',
          gateInAt: new Date().toISOString(),
          sealNo: 'SEAL-9988',
          shipperTaxCode: '0401889988',
        },
      };

      await cargoNotificationService.ingestEvent(gateInEvent);

      // Kiểm tra reminder CY_CUTOFF đã chuyển sang cancelled
      const [remAfter] = await sequelize.query(`
        SELECT count(*)::int AS count FROM cargo_reminders
        WHERE target_type = 'CY_CUTOFF' AND target_id = :bookingNo AND status = 'cancelled'
      `, { replacements: { bookingNo: testBookingNo }, type: sequelize.QueryTypes.SELECT });
      expect(remAfter.count).toBeGreaterThan(0);
    });
  });

  describe('4. Hàng đợi ngoại lệ Người nhận chưa có tài khoản (Mục 4.2)', () => {
    it('Manifest chứa Consignee chưa có trong hệ thống được tự động đưa vào hàng đợi ngoại lệ', async () => {
      const unknownConsignee = `Công ty TNHH Chưa Có Tài Khoản ${Date.now()}`;
      const unknownTaxCode = `9999${Date.now().toString().slice(-6)}`;

      const manifestEvent = {
        eventId: `evt-manifest-unclaimed-1`,
        eventCode: 'ManifestSent',
        direction: 'import',
        payload: {
          manifestId: `MNF-${Date.now()}`,
          blNumber: `BL-UNCLAIMED-${Date.now()}`,
          consigneeName: unknownConsignee,
          consigneeTaxCode: unknownTaxCode,
          vesselName: 'ONE CONTINUITY',
          voyageNo: '2602S',
          eta: new Date(Date.now() + 86400000).toISOString(),
          containers: ['CONT1', 'CONT2'],
        },
      };

      await cargoNotificationService.ingestEvent(manifestEvent);

      const queue = await cargoNotificationService.getUnclaimedConsignees();
      const match = queue.find(x => x.consignee_tax_code === unknownTaxCode);
      expect(match).toBeDefined();
      expect(match.consignee_name).toBe(unknownConsignee);
      expect(match.status).toBe('PENDING_CLAIM');
    });
  });

  describe('5. To-Do List & Document Viewer (Nhiệm vụ cốt lõi Người 1)', () => {
    it('getTodoList trả về danh sách công việc cần làm có phân loại xuất/nhập', async () => {
      const todos = await cargoNotificationService.getTodoList({ type: 'all' });
      expect(Array.isArray(todos)).toBe(true);
      if (todos.length > 0) {
        expect(todos[0]).toHaveProperty('taskType');
        expect(todos[0]).toHaveProperty('priority');
        expect(todos[0]).toHaveProperty('actionUrl');
      }
    });

    it('getDocumentDetail trả về đúng chi tiết phiếu e-EIR', async () => {
      // Tìm một EIR có sẵn trong DB
      const [eir] = await sequelize.query('SELECT reference_no FROM eir_transactions LIMIT 1', {
        type: sequelize.QueryTypes.SELECT,
      });

      if (eir?.reference_no) {
        const detail = await cargoNotificationService.getDocumentDetail('EIR', eir.reference_no);
        expect(detail.documentType).toBe('EIR');
        expect(detail.referenceNo).toBe(eir.reference_no);
        expect(detail).toHaveProperty('qrData');
        expect(detail).toHaveProperty('actionRecommendation');
      }
    });

    it('getDocumentDetail trả về đúng chi tiết e-D/O từ dữ liệu hãng tàu', async () => {
      const dos = require('../src/modules/shipping-line/shippingLine.service').deliveryOrders || [];
      if (dos.length > 0) {
        const testDO = dos[0];
        const detail = await cargoNotificationService.getDocumentDetail('EDO', testDO.doNumber);
        expect(detail.documentType).toBe('EDO');
        expect(detail.doNumber).toBe(testDO.doNumber);
        expect(detail).toHaveProperty('emptyReturnDepot');
        expect(detail).toHaveProperty('qrData');
      }
    });
  });
});

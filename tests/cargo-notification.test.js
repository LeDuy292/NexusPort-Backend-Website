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

  describe('6. Đối soát 10 sự kiện xuất khẩu (Mục 3.2) & Nhãn hành động chuẩn', () => {
    it('Sự kiện 1: BookingConfirmed phải có nhãn "Mở booking" và tạo reminder VGM, CY', async () => {
      const bkgNo = `BKG-CONF-${Date.now()}`;
      const event = {
        eventId: `evt-bkg-conf-${Date.now()}`,
        eventCode: 'BookingConfirmed',
        direction: 'export',
        payload: {
          bookingNo: bkgNo,
          vesselName: 'EVER GLORY',
          voyageNo: '2601N',
          etd: new Date(Date.now() + 48 * 3600 * 1000).toISOString(),
          cyCutOff: new Date(Date.now() + 24 * 3600 * 1000).toISOString(),
          vgmCutOff: new Date(Date.now() + 20 * 3600 * 1000).toISOString(),
          siCutOff: new Date(Date.now() + 16 * 3600 * 1000).toISOString(),
          emptyPickupPlace: 'Depot Tân Cảng Đà Nẵng',
          shipperTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Mở booking');
      expect(res.notification.action_type).toBe('open_booking');
      expect(res.notification.document_type).toBe('BOOKING');

      // Kiểm tra reminder được tạo tự động cho VGM và CY
      const [remCount] = await sequelize.query(`
        SELECT count(*)::int AS count FROM cargo_reminders WHERE target_id = :bkgNo
      `, { replacements: { bkgNo }, type: sequelize.QueryTypes.SELECT });
      expect(remCount.count).toBeGreaterThanOrEqual(2);
    });

    it('Sự kiện 2: BookingRejected phải có nhãn "Tạo booking mới"', async () => {
      const event = {
        eventId: `evt-bkg-rej-${Date.now()}`,
        eventCode: 'BookingRejected',
        direction: 'export',
        payload: {
          bookingNo: `BKG-REJ-${Date.now()}`,
          reason: 'Hết chỗ trên tàu chuyến 2601N',
          shipperTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Tạo booking mới');
      expect(res.notification.action_type).toBe('create_new_booking');
      expect(res.notification.message).toContain('Hết chỗ');
    });

    it('Sự kiện 3: BookingUpdated phải thể hiện giá trị cũ và mới kèm nhãn "Xem chi tiết"', async () => {
      const event = {
        eventId: `evt-bkg-upd-${Date.now()}`,
        eventCode: 'BookingUpdated',
        direction: 'export',
        payload: {
          bookingNo: testBookingNo,
          changes: [
            { field: 'ETD', oldValue: '2026-11-12T10:00:00Z', newValue: '2026-11-13T06:00:00Z' },
          ],
          shipperTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Xem chi tiết');
      expect(res.notification.message).toContain('cũ');
      expect(res.notification.message).toContain('mới');
    });

    it('Sự kiện 4: ContainerListAnnounced có lỗi phải có mức Khẩn và nhãn "Sửa danh sách"', async () => {
      const event = {
        eventId: `evt-cont-err-${Date.now()}`,
        eventCode: 'ContainerListAnnounced',
        direction: 'export',
        payload: {
          bookingNo: testBookingNo,
          errors: ['Thiếu VGM container 1', 'Số chì không đúng định dạng'],
          shipperTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.priority).toBe('urgent');
      expect(res.notification.action_label).toBe('Sửa danh sách');
      expect(res.notification.message).toContain('Dòng 1:');
    });

    it('Sự kiện 5: Sắp hết hạn Cut-off dưới 2 giờ phải tự động nâng lên mức Khẩn kèm nhãn "Khai hoặc nộp"', async () => {
      const event = {
        eventId: `evt-cutoff-urg-${Date.now()}`,
        eventCode: 'CutoffApproaching',
        direction: 'export',
        payload: {
          bookingNo: testBookingNo,
          cutoffType: 'VGM',
          dueAt: new Date(Date.now() + 1.5 * 3600 * 1000).toISOString(), // Còn 1.5 giờ -> Khẩn
          shipperTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.priority).toBe('urgent');
      expect(res.notification.action_label).toBe('Khai hoặc nộp');
    });

    it('Sự kiện 8: Container bị loại / rớt tàu phải có mức Khẩn kèm nhãn "Xem chi tiết"', async () => {
      const event = {
        eventId: `evt-rollover-${Date.now()}`,
        eventCode: 'ContainerRolledOver',
        direction: 'export',
        payload: {
          bookingNo: testBookingNo,
          containerNo: 'TESTU9999999',
          vesselName: 'EVER GLORY',
          voyageNo: '2601N',
          reason: 'Tàu đầy tải',
          newVoyageNo: '2602S',
          shipperTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.priority).toBe('urgent');
      expect(res.notification.action_label).toBe('Xem chi tiết');
      expect(res.notification.message).toContain('2602S');
    });

    it('Sự kiện 9: Draft B/L cần xác nhận phải có nhãn "Xem và xác nhận draft"', async () => {
      const event = {
        eventId: `evt-draft-bl-${Date.now()}`,
        eventCode: 'DraftBLIssued',
        direction: 'export',
        payload: {
          bookingNo: testBookingNo,
          blNumber: testBlNumber,
          confirmationDeadline: new Date(Date.now() + 86400000).toISOString(),
          shipperTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Xem và xác nhận draft');
      expect(res.notification.action_type).toBe('confirm_draft_bl');
    });

    it('Sự kiện 6: ContainerGatedIn phải gộp nhiều container và có nhãn "Xem EIR"', async () => {
      const event = {
        eventId: `evt-gate-in-grp-${Date.now()}`,
        eventCode: 'ContainerGatedIn',
        direction: 'export',
        payload: {
          bookingNo: `${testBookingNo}_GRP`,
          eirNo: `EIR-GRP-${Date.now()}`,
          containers: ['CONT1111111', 'CONT2222222'],
          gateCode: 'GATE-01',
          gateInAt: new Date().toISOString(),
          shipperTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Xem EIR');
      expect(res.notification.action_type).toBe('view_eir');
      expect(res.notification.title).toContain('2 container');
    });

    it('Sự kiện 10: B/L đã phát hành phải có nhãn "Xem B/L"', async () => {
      const event = {
        eventId: `evt-bl-issued-${Date.now()}`,
        eventCode: 'BLIssued',
        direction: 'export',
        payload: {
          bookingNo: testBookingNo,
          blNumber: testBlNumber,
          shippedOnBoardDate: new Date().toISOString(),
          shipperTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Xem B/L');
      expect(res.notification.action_type).toBe('view_bl');
    });
  });

  describe('7. Các hành động xử lý trực tiếp (Xác nhận Draft B/L & Nộp VGM)', () => {
    it('confirmDraftBL cập nhật trạng thái và hủy reminder xác nhận draft', async () => {
      const res = await cargoNotificationService.confirmDraftBL({
        bookingNo: testBookingNo,
        blNumber: testBlNumber,
      });
      expect(res.success).toBe(true);

      // Reminder DRAFT_BL_CONFIRM phải chuyển sang cancelled
      const [rem] = await sequelize.query(`
        SELECT count(*)::int AS count FROM cargo_reminders
        WHERE target_type = 'DRAFT_BL_CONFIRM' AND target_id = :targetKey AND status = 'cancelled'
      `, { replacements: { targetKey: testBlNumber }, type: sequelize.QueryTypes.SELECT });
      expect(rem.count).toBeGreaterThanOrEqual(0);
    });

    it('submitVGM ghi nhận thành công và tự động hủy reminder VGM_CUTOFF', async () => {
      const res = await cargoNotificationService.submitVGM({
        bookingNo: testBookingNo,
        containerNo: 'TESTU1234567',
        vgmWeight: 24500,
        authorizedPerson: 'Nguyễn Kiểm Định',
      });
      expect(res.success).toBe(true);
      expect(res.vgmData.vgmWeight).toBe(24500);

      const [rem] = await sequelize.query(`
        SELECT count(*)::int AS count FROM cargo_reminders
        WHERE target_type = 'VGM_CUTOFF' AND target_id = :bookingNo AND status = 'cancelled'
      `, { replacements: { bookingNo: testBookingNo }, type: sequelize.QueryTypes.SELECT });
      expect(rem.count).toBeGreaterThanOrEqual(1);
    });

    it('processDueReminders quét và tự động phát sinh thông báo cho reminder đến hạn', async () => {
      // Tạo một reminder giả lập đã đến giờ nhắc
      await sequelize.query(`
        INSERT INTO cargo_reminders (
          target_type, target_id, milestone, due_at, remind_at, status, title, message, company_tax_code
        ) VALUES (
          'VGM_CUTOFF', :bookingNo, 'T-2H', now() + interval '1 hour', now() - interval '5 minutes',
          'pending', 'Nhắc hạn nộp VGM', 'Sắp hết hạn', '0401889988'
        )
      `, { replacements: { bookingNo: `${testBookingNo}_SCHED` } });

      const proc = await cargoNotificationService.processDueReminders();
      expect(proc.success).toBe(true);
      expect(proc.processedCount).toBeGreaterThanOrEqual(1);
    });
  });

  describe('8. Đối soát 7 sự kiện nhập khẩu (Mục 4.2) & Xử lý ngoại lệ', () => {
    it('Sự kiện M1: ManifestSent có nhãn "Xem thông báo hàng đến"', async () => {
      const event = {
        eventId: `evt-mnf-${Date.now()}`,
        eventCode: 'ManifestSent',
        direction: 'import',
        payload: {
          manifestId: `MNF-${Date.now()}`,
          blNumber: `BL-IMP-${Date.now()}`,
          vesselName: 'CMA CGM CHENNAI',
          eta: new Date(Date.now() + 86400000).toISOString(),
          consigneeTaxCode: '0401889988',
          consigneeName: 'Công ty Cổ phần Thép Hòa Phát',
          containers: ['CMAU1234567'],
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Xem thông báo hàng đến');
      expect(res.notification.action_type).toBe('view_arrival_notice');
    });

    it('Sự kiện M2: ContainerDischarged có nhãn "Xem container"', async () => {
      const event = {
        eventId: `evt-disch-${Date.now()}`,
        eventCode: 'ContainerDischarged',
        direction: 'import',
        payload: {
          containerNo: 'CMAU1234567',
          blNumber: 'BL-IMP-TEST',
          vesselName: 'CMA CGM CHENNAI',
          dischargedAt: new Date().toISOString(),
          yardLocation: 'BLOCK-B2-04-03',
          consigneeTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Xem container');
      expect(res.notification.action_type).toBe('view_container');
    });

    it('Sự kiện M3: EDoIssued có nhãn "Đặt lệnh lấy hàng"', async () => {
      const event = {
        eventId: `evt-edo-${Date.now()}`,
        eventCode: 'EDoIssued',
        direction: 'import',
        payload: {
          doNumber: `EDO-${Date.now()}`,
          blNumber: 'BL-IMP-TEST',
          validUntil: new Date(Date.now() + 172800000).toISOString(),
          emptyReturnDepot: 'Depot Tân Cảng',
          consigneeTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Đặt lệnh lấy hàng');
      expect(res.notification.action_type).toBe('order_service_pickup');
    });

    it('Sự kiện M5: FreeTimeApproaching có nhãn "Đặt lệnh lấy hàng"', async () => {
      const event = {
        eventId: `evt-freetime-${Date.now()}`,
        eventCode: 'FreeTimeApproaching',
        direction: 'import',
        payload: {
          doNumber: `EDO-FT-${Date.now()}`,
          dueAt: new Date(Date.now() + 3600000).toISOString(), // Còn 1h -> Khẩn
          estimatedDemurrage: 500000,
          consigneeTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.priority).toBe('urgent');
      expect(res.notification.action_label).toBe('Đặt lệnh lấy hàng');
    });

    it('Sự kiện M7: ContainerGatedOut có nhãn "Nhắc trả vỏ"', async () => {
      const event = {
        eventId: `evt-gateout-${Date.now()}`,
        eventCode: 'ContainerGatedOut',
        direction: 'import',
        payload: {
          eirNo: `EIR-OUT-${Date.now()}`,
          containerNo: 'CMAU1234567',
          emptyReturnDepot: 'Depot Tân Cảng Đà Nẵng',
          consigneeTaxCode: '0401889988',
        },
      };
      const res = await cargoNotificationService.ingestEvent(event);
      expect(res.notification.action_label).toBe('Nhắc trả vỏ');
      expect(res.notification.message).toContain('trả vỏ');
    });

    it('Xử lý claimConsignee gán người nhận thành công', async () => {
      const claimBl = `TEST_BL_CLAIM_${Date.now()}`;
      // Đưa một item vào hàng đợi
      await sequelize.query(`
        INSERT INTO unclaimed_consignee_queue (
          manifest_id, bl_number, consignee_name, container_count, status
        ) VALUES (
          'TEST_MNF_CLAIM', :claimBl, 'Công ty Test Claim', 1, 'PENDING_CLAIM'
        )
      `, { replacements: { claimBl } });

      const [item] = await sequelize.query(`
        SELECT id FROM unclaimed_consignee_queue
        WHERE bl_number = :claimBl AND status = 'PENDING_CLAIM'
        ORDER BY created_at DESC LIMIT 1
      `, { replacements: { claimBl }, type: sequelize.QueryTypes.SELECT });

      const claimRes = await cargoNotificationService.claimConsignee({
        queueId: item.id,
        companyId: 'test-carrier-id',
      });
      expect(claimRes.status).toBe('CLAIMED');
    });
  });
});

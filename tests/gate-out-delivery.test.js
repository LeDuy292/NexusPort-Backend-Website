'use strict';

const path = require('path');
require('dotenv').config({ path: path.join(__dirname, '../.env') });
const gateOutDeliveryService = require('../src/modules/yard/gate-out-delivery.service');
const { YardGateOutRequest } = require('../src/models/YardGateOutRequest');

describe('Gate-Out Delivery & e-EIR Flow (Giao container tại cổng ra)', () => {
  let testContId;
  let testBooking;
  let testPlate;
  let testDriver;
  let createdReq;

  beforeAll(async () => {
    testContId = `TEST${Math.floor(1000000 + Math.random() * 9000000)}`;
    testBooking = `BK-TEST-${Date.now()}`;
    testPlate = '43C-999.88';
    testDriver = 'Nguyễn Văn Kiểm Tra';

    // Tạo request mẫu trong DB
    createdReq = await YardGateOutRequest.create({
      container_id: testContId,
      container_type: '40FT HC',
      cargo_type: 'Hàng Tiêu Dùng Xuất Nhập Khẩu',
      driver_name: testDriver,
      driver_phone: '0905123999',
      truck_plate: testPlate,
      gate_name: 'Cổng A',
      booking_code: testBooking,
      carrier_name: 'Hapag-Lloyd',
      vessel_name: 'EVER GIVEN',
      weight_kg: '26,500 KG',
      seal_number: 'SL-TEST-8812',
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
    });
  });

  afterAll(async () => {
    if (createdReq?.id) {
      await YardGateOutRequest.destroy({ where: { id: createdReq.id } }).catch(() => {});
    }
  });

  describe('1. Kiểm tra 5 điều kiện tại cổng vào (Real-time Eligibility Check)', () => {
    it('Phải từ chối nếu biển số xe thực tế không khớp với đăng ký trên lệnh', async () => {
      const res = await gateOutDeliveryService.checkEligibility({
        containerId: testContId,
        bookingCode: testBooking,
        truckPlate: '51A-999.99', // Sai biển số
        driverName: testDriver,
      });

      expect(res.success).toBe(true);
      expect(res.eligible).toBe(false);
      expect(res.checks.vehicleDriverValid.pass).toBe(false);
      expect(res.rejectionReason).toContain('Xe sai');
    });

    it('Phải từ chối nếu container bị cont khác đè (chưa sẵn sàng giao)', async () => {
      createdReq.blocking_containers = 2;
      await createdReq.save();

      const res = await gateOutDeliveryService.checkEligibility({
        containerId: testContId,
        bookingCode: testBooking,
        truckPlate: testPlate,
        driverName: testDriver,
      });

      expect(res.checks.containerReady.pass).toBe(false);
      expect(res.checks.containerReady.status).toBe('WAITING_YARD_READY');

      // Khôi phục lại
      createdReq.blocking_containers = 0;
      await createdReq.save();
    });

    it('Phải đạt đủ 5/5 điều kiện khi thông tin khớp và container sẵn sàng', async () => {
      const res = await gateOutDeliveryService.checkEligibility({
        containerId: testContId,
        bookingCode: testBooking,
        truckPlate: testPlate,
        driverName: testDriver,
      });

      expect(res.success).toBe(true);
      expect(res.checks.paymentValid.pass).toBe(true);
      expect(res.checks.timeSlotValid.pass).toBe(true);
      expect(res.checks.vehicleDriverValid.pass).toBe(true);
      expect(res.checks.containerReady.pass).toBe(true);
    });
  });

  describe('2. Lập EIR điện tử hướng ra (Issue Gate-Out e-EIR)', () => {
    it('Phát hành thành công phiếu e-EIR bất biến kèm ghi nhận hư hỏng vỏ (nếu có)', async () => {
      const damageDetails = [
        { face: 'Cửa container', severity: 'Vừa', description: 'Móp cạnh cửa bên phải 15cm', photoUrl: 'https://cdn.nexusport.io.vn/photos/dmg-door-01.jpg' }
      ];

      const res = await gateOutDeliveryService.issueGateOutEir({
        yardRequestId: createdReq.id,
        containerId: testContId,
        sealNumber: 'SL-TEST-8812',
        hasDamage: true,
        damageDetails,
        truckPlate: testPlate,
        driverName: testDriver,
        terminalSignatory: 'Trần Văn Hùng (Điều Độ)',
        driverSignatory: testDriver,
      });

      expect(res.success).toBe(true);
      expect(res.eir.referenceNo).toMatch(/^EIR-OUT-\d{8}-[A-Z0-9]{4}$/);
      expect(res.eir.activity).toBe('out');
      expect(res.eir.hasDamage).toBe(true);
      expect(res.eir.qrData).toContain('NXP-EIR|');

      // Kiểm tra slot bãi đã được giải phóng sang Available
      const reloaded = await YardGateOutRequest.findByPk(createdReq.id);
      expect(reloaded.slot_status).toBe('Available');
      expect(reloaded.status).toBe('READY_FOR_GATE_OUT');
      expect(reloaded.flow_step).toBe(4);
    });
  });

  describe('3. Xử lý ngoại lệ (Exception Handling)', () => {
    it('Xử lý ngoại lệ phút chót EDoHeld: Hạ container về slot, hủy lệnh, phục hồi Occupied', async () => {
      const res = await gateOutDeliveryService.confirmGateOutExit({
        yardRequestId: createdReq.id,
        simulateHeld: true, // Giả lập nhận được EDoHeld phút chót
      });

      expect(res.success).toBe(false);
      expect(res.cancelled).toBe(true);
      expect(res.status).toBe('CANCELLED_EDO_HELD');

      const reloaded = await YardGateOutRequest.findByPk(createdReq.id);
      expect(reloaded.status).toBe('CANCELLED_EDO_HELD');
      expect(reloaded.slot_status).toBe('Occupied'); // Khôi phục slot
    });

    it('Xử lý ngoại lệ sai số chì niêm phong', async () => {
      const res = await gateOutDeliveryService.handleException({
        type: 'SEAL_MISMATCH',
        yardRequestId: createdReq.id,
        reason: 'Số chì thực tế trên xe là SL-99999, khác với SL-TEST-8812',
      });

      expect(res.success).toBe(true);
      expect(res.exceptionType).toBe('SEAL_MISMATCH');
      expect(res.actionTaken).toContain('báo điện tử tới Hãng tàu');
    });
  });

  describe('4. Xác nhận xuất cổng & Quy tắc một container chỉ giao một lần', () => {
    it('Ghi nhận giờ ra cổng, chốt lưu bãi, xuất hóa đơn và nhắc trả vỏ về depot', async () => {
      // Đặt lại status để test xuất cổng thành công
      createdReq.status = 'READY_FOR_GATE_OUT';
      await createdReq.save();

      const res = await gateOutDeliveryService.confirmGateOutExit({
        yardRequestId: createdReq.id,
        eirReferenceNo: 'EIR-OUT-20261117-TEST',
        gateName: 'Cổng Ra 01',
      });

      expect(res.success).toBe(true);
      expect(res.data.status).toBe('COMPLETED');
      expect(res.data.invoice).toBeDefined();
      expect(res.data.emptyReturnReminder.depotName).toContain('Tiên Sa');

      const reloaded = await YardGateOutRequest.findByPk(createdReq.id);
      expect(reloaded.status).toBe('COMPLETED');
    });

    it('Quy tắc: Không cho phép giao lại một container đã hoàn tất ra cổng', async () => {
      const res = await gateOutDeliveryService.checkEligibility({
        containerId: testContId,
        bookingCode: testBooking,
        truckPlate: testPlate,
        driverName: testDriver,
      });

      expect(res.eligible).toBe(false);
      expect(res.status).toBe('ALREADY_GATED_OUT');
      expect(res.rejectionReason).toContain('chỉ giao một lần');
    });
  });
});

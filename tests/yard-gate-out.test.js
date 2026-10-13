'use strict';

const request = require('supertest');
const http = require('http');
const ioClient = require('socket.io-client');
const app = require('../src/app');
const { initSocket } = require('../src/config/socket');
const { YardGateOutRequest } = require('../src/models/YardGateOutRequest');
const { User } = require('../src/models/User');
const { generateToken } = require('../src/utils/jwt');
const { syncDB } = require('../src/config/database');

describe('Yard Outbound Container Confirmation API (Xác Nhận Container Xuất Bãi)', () => {
  let server;
  let serverUrl;
  let io;

  const yardStaffId = '11111111-2222-3333-4444-555555555555';
  const gateOfficerId = '22222222-3333-4444-5555-666666666666';
  const driverId = '33333333-4444-5555-6666-777777777777';

  let yardStaffToken;
  let gateOfficerToken;
  let driverToken;

  let testRequest;

  beforeAll(async () => {
    await syncDB({ alter: true });

    // Seed test users
    await User.findOrCreate({
      where: { id: yardStaffId },
      defaults: {
        username: 'yard_staff_test',
        password: 'password123',
        email: 'yard_staff_test@example.com',
        role: 'Yard Operator',
      },
    });

    await User.findOrCreate({
      where: { id: gateOfficerId },
      defaults: {
        username: 'gate_officer_test',
        password: 'password123',
        email: 'gate_officer_test@example.com',
        role: 'Gate Officer',
      },
    });

    await User.findOrCreate({
      where: { id: driverId },
      defaults: {
        username: 'driver_outbound_test',
        password: 'password123',
        email: 'driver_outbound_test@example.com',
        role: 'Driver',
      },
    });

    yardStaffToken = generateToken({ id: yardStaffId, username: 'yard_staff_test', role: 'Yard Operator' });
    gateOfficerToken = generateToken({ id: gateOfficerId, username: 'gate_officer_test', role: 'Gate Officer' });
    driverToken = generateToken({ id: driverId, username: 'driver_outbound_test', role: 'Driver' });

    // Start HTTP server with Socket.IO for realtime checks
    server = http.createServer(app);
    io = initSocket(server);

    await new Promise((resolve) => {
      server.listen(0, () => {
        const port = server.address().port;
        serverUrl = `http://localhost:${port}`;
        resolve();
      });
    });

    // Seed test outbound request
    testRequest = await YardGateOutRequest.create({
      container_id: 'TEST-CONT-9988',
      container_type: '40FT HC',
      cargo_type: 'Linh kiện điện tử',
      driver_name: 'Đặng Văn Test',
      driver_phone: '0905123999',
      driver_id: driverId,
      truck_plate: '43C-999.11',
      gate_name: 'Cổng A',
      booking_code: 'BK-TEST-9988',
      seal_number: 'SEAL-VN-887711',
      yard_block: 'B',
      yard_bay: '04',
      yard_row: '10',
      yard_tier: '2',
      yard_slot: 'B-04-10-2',
      slot_status: 'Occupied',
      status: 'PENDING',
      urgency: 'HIGH',
      condition: 'Tốt (Không Hư Hỏng)',
      rtg_crane: 'RTG-05',
      rtg_operator: 'Trần Văn Hùng',
      flow_step: 0,
    });
  });

  afterAll(async () => {
    if (testRequest?.id) {
      await YardGateOutRequest.destroy({ where: { id: testRequest.id } }).catch(() => {});
    }
    if (io) {
      await new Promise((resolve) => io.close(resolve));
    }
    if (server) {
      await new Promise((resolve) => server.close(resolve));
    }
  });

  describe('GET /api/yard/gate-out-requests', () => {
    it('TC-YGO-01: returns 401 when not authenticated', async () => {
      const res = await request(app).get('/api/yard/gate-out-requests');
      expect(res.status).toBe(401);
    });

    it('TC-YGO-02: returns list of pending gate-out requests for yard staff', async () => {
      const res = await request(app)
        .get('/api/yard/gate-out-requests')
        .set('Authorization', `Bearer ${yardStaffToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.requests).toBeDefined();
      expect(Array.isArray(res.body.data.requests)).toBe(true);
      expect(res.body.data.requests.length).toBeGreaterThan(0);

      const found = res.body.data.requests.find((r) => r.containerId === 'TEST-CONT-9988');
      expect(found).toBeDefined();
      expect(found.truck).toBe('43C-999.11');
      expect(found.position).toBe('B-04-10-2');
      expect(found.slotStatus).toBe('Occupied');
    });

    it('TC-YGO-03: filters by status=PENDING properly', async () => {
      const res = await request(app)
        .get('/api/yard/gate-out-requests?status=PENDING')
        .set('Authorization', `Bearer ${yardStaffToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      res.body.data.requests.forEach((r) => {
        expect(['PENDING', 'CHỜ TIẾP NHẬN']).toContain(r.status);
      });
    });
  });

  describe('GET /api/yard/gate-out-requests/:id', () => {
    it('TC-YGO-04: returns detailed container, yard slot location, and RTG equipment info', async () => {
      const res = await request(app)
        .get(`/api/yard/gate-out-requests/${testRequest.id}`)
        .set('Authorization', `Bearer ${yardStaffToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      const reqData = res.body.data;

      expect(reqData.containerId).toBe('TEST-CONT-9988');
      expect(reqData.sealNumber).toBe('SEAL-VN-887711');
      expect(reqData.block).toBe('B');
      expect(reqData.bay).toBe('04');
      expect(reqData.row).toBe('10');
      expect(reqData.tier).toBe('2');
      expect(reqData.position).toBe('B-04-10-2');
      expect(reqData.rtgCrane).toBe('RTG-05');
      expect(reqData.rtgOperator).toBe('Trần Văn Hùng');
      expect(reqData.slotStatus).toBe('Occupied');
    });

    it('TC-YGO-05: returns 404 when request ID not found', async () => {
      const res = await request(app)
        .get('/api/yard/gate-out-requests/99999999-9999-9999-9999-999999999999')
        .set('Authorization', `Bearer ${yardStaffToken}`);

      expect(res.status).toBe(404);
      expect(res.body.success).toBe(false);
    });
  });

  describe('POST /api/yard/gate-out-requests/:id/lift-order', () => {
    it('TC-YGO-06: assigns RTG crane and updates status to IN_PROGRESS', async () => {
      const res = await request(app)
        .post(`/api/yard/gate-out-requests/${testRequest.id}/lift-order`)
        .set('Authorization', `Bearer ${yardStaffToken}`)
        .send({
          rtgCrane: 'RTG-01',
          rtgOperator: 'Nguyễn Văn Cẩu',
        });

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.rtgCrane).toBe('RTG-01');
      expect(res.body.data.rtgOperator).toBe('Nguyễn Văn Cẩu');
      expect(res.body.data.flowStep).toBe(1);
      expect(res.body.data.statusCode).toBe('IN_PROGRESS');
    });
  });

  describe('PATCH /api/yard/gate-out-requests/:id/step', () => {
    it('TC-YGO-07: advances flow step to arrived at slot (step=2) and verifying (step=3)', async () => {
      const resStep2 = await request(app)
        .patch(`/api/yard/gate-out-requests/${testRequest.id}/step`)
        .set('Authorization', `Bearer ${yardStaffToken}`)
        .send({ step: 2, note: 'Cẩu đã đến vị trí ô B-04-10-2' });

      expect(resStep2.status).toBe(200);
      expect(resStep2.body.data.flowStep).toBe(2);

      const resStep3 = await request(app)
        .patch(`/api/yard/gate-out-requests/${testRequest.id}/step`)
        .set('Authorization', `Bearer ${yardStaffToken}`)
        .send({ step: 3, note: 'Bắt đầu kiểm tra mã container và số chì' });

      expect(resStep3.status).toBe(200);
      expect(resStep3.body.data.flowStep).toBe(3);
    });
  });

  describe('POST /api/yard/gate-out-requests/:id/confirm-loaded', () => {
    it('TC-YGO-08: rejects confirmation if container ID verification is missing', async () => {
      const res = await request(app)
        .post(`/api/yard/gate-out-requests/${testRequest.id}/confirm-loaded`)
        .set('Authorization', `Bearer ${yardStaffToken}`)
        .send({
          verifiedContainerId: false,
          verifiedSeal: true,
          verifiedNoDamage: true,
        });

      expect(res.status).toBe(400);
      expect(res.body.message).toContain('Mã Container');
    });

    it('TC-YGO-09: rejects confirmation if seal verification is missing', async () => {
      const res = await request(app)
        .post(`/api/yard/gate-out-requests/${testRequest.id}/confirm-loaded`)
        .set('Authorization', `Bearer ${yardStaffToken}`)
        .send({
          verifiedContainerId: true,
          verifiedSeal: false,
          verifiedNoDamage: true,
        });

      expect(res.status).toBe(400);
      expect(res.body.message).toContain('Số Chì Niêm Phong');
    });

    it('TC-YGO-10: successfully confirms loaded, updates status to READY_FOR_GATE_OUT and RELEASES SLOT to Available', async () => {
      const res = await request(app)
        .post(`/api/yard/gate-out-requests/${testRequest.id}/confirm-loaded`)
        .set('Authorization', `Bearer ${yardStaffToken}`)
        .send({
          verifiedContainerId: true,
          verifiedSeal: true,
          verifiedNoDamage: true,
          sealNumber: 'SEAL-VN-887711',
          containerCondition: 'Tốt (Không Hư Hỏng)',
          note: 'Container đã gắp và khóa ngù chốt trên rơ-moóc an toàn.',
        });

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      const data = res.body.data;

      // Acceptance Criteria 4: container Ready for Gate Out
      expect(data.statusCode).toBe('READY_FOR_GATE_OUT');
      expect(data.flowStep).toBe(4);

      // Acceptance Criteria 5: Slot becomes Available & releasedAt is set
      expect(data.slotStatus).toBe('Available');
      expect(data.releasedAt).toBeDefined();

      // Inspection details
      expect(data.verifiedContainerId).toBe(true);
      expect(data.verifiedSeal).toBe(true);
      expect(data.verifiedNoDamage).toBe(true);
      expect(data.loadedAt).toBeDefined();
    });

    it('TC-YGO-11: gate staff receives realtime socket event when container is ready for gate out', (done) => {
      const gateSocket = ioClient(serverUrl, {
        auth: { token: gateOfficerToken },
        transports: ['websocket'],
      });

      gateSocket.on('connect', async () => {
        gateSocket.on('gate:container_ready', (event) => {
          try {
            expect(event.containerId).toBe('TEST-CONT-9988');
            expect(event.truckPlate).toBe('43C-999.11');
            expect(event.status).toBe('READY_FOR_GATE_OUT');
            expect(event.slotStatus).toBe('Available');
            gateSocket.disconnect();
            done();
          } catch (err) {
            gateSocket.disconnect();
            done(err);
          }
        });

        // Trigger confirmation again to verify socket emission
        await request(app)
          .post(`/api/yard/gate-out-requests/${testRequest.id}/confirm-loaded`)
          .set('Authorization', `Bearer ${yardStaffToken}`)
          .send({
            verifiedContainerId: true,
            verifiedSeal: true,
            verifiedNoDamage: true,
          });
      });
    });
  });
});

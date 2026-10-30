'use strict';

const request = require('supertest');
const app = require('../src/app');
const { TransportTrip, TransportTripHistory } = require('../src/models/TransportTrip');
const { User } = require('../src/models/User');
const { generateToken } = require('../src/utils/jwt');
const { syncDB } = require('../src/config/database');

describe('NXP-137: Driver Transport Trip Full Lifecycle & Status Transitions', () => {
  const driverId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
  let driverToken;
  let tripId;

  beforeAll(async () => {
    await syncDB({ alter: true });

    await User.findOrCreate({
      where: { id: driverId },
      defaults: {
        username: 'driver_lifecycle_test',
        password: 'password123',
        email: 'driver_lifecycle_test@example.com',
        role: 'Driver',
      },
    });

    driverToken = generateToken({ id: driverId, username: 'driver_lifecycle_test', role: 'Driver' });

    const trip = await TransportTrip.create({
      trip_code: `TEST-LIFE-${Date.now()}`,
      driver_id: driverId,
      truck_plate: '43C-888.88',
      container_no: 'CONT-LIFE-1234',
      container_type: '40HC',
      cargo_type: 'Hàng May Mặc',
      gate_in_code: 'GATE-A1',
      gate_out_code: 'GATE-B1',
      yard_block: 'Block A',
      yard_slot: 'A05-12',
      status: 'Assigned',
    });

    tripId = trip.id;
  });

  afterAll(async () => {
    if (tripId) {
      await TransportTripHistory.destroy({ where: { trip_id: tripId } });
      await TransportTrip.destroy({ where: { id: tripId } });
    }
  });

  it('Step 0 - Rule Check: Cannot skip steps (Calling gate-in when Assigned returns 409)', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/gate-in`)
      .set('Authorization', `Bearer ${driverToken}`)
      .send({ note: 'Cố tình nhảy cóc bước' });

    expect(res.status).toBe(409);
    expect(res.body.success).toBe(false);
    expect(res.body.message).toContain('Không thể thực hiện bước này');
  });

  it('Step 1: Driver acknowledges trip (Assigned → Acknowledged)', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/acknowledge`)
      .set('Authorization', `Bearer ${driverToken}`);

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.trip.status).toBe('Acknowledged');
    expect(res.body.data.trip.nextStep).toBe('Vehicle Picked Up');
  });

  it('Step 2: Driver picks up vehicle (Acknowledged → Vehicle Picked Up)', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/pickup-vehicle`)
      .set('Authorization', `Bearer ${driverToken}`)
      .send({ note: 'Đã nhận xe đầu kéo tại bãi xe' });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.trip.status).toBe('Vehicle Picked Up');
    expect(res.body.data.trip.nextStep).toBe('Container Picked Up');
  });

  it('Step 3: Driver picks up container (Vehicle Picked Up → Container Picked Up)', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/pickup-container`)
      .set('Authorization', `Bearer ${driverToken}`)
      .send({ note: 'Đã gắn container lên rơ-mooc' });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.trip.status).toBe('Container Picked Up');
    expect(res.body.data.trip.nextStep).toBe('Gate In');
  });

  it('Step 4: Driver confirms Gate In (Container Picked Up → In Transit)', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/gate-in`)
      .set('Authorization', `Bearer ${driverToken}`)
      .send({ note: 'Đã qua cổng kiểm soát Gate A1' });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.trip.status).toBe('In Transit');
    expect(res.body.data.trip.nextStep).toBe('Arrived at Yard');
  });

  it('Step 5: Driver arrives at yard (In Transit → Arrived at Yard)', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/arrive-yard`)
      .set('Authorization', `Bearer ${driverToken}`)
      .send({ note: 'Đã đến vị trí ô bãi Block A' });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.trip.status).toBe('Arrived at Yard');
    expect(res.body.data.trip.nextStep).toBe('Container Delivered');
  });

  it('Step 6: Driver delivers container with seal, condition, recipient (Arrived at Yard → Container Delivered)', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/deliver-container`)
      .set('Authorization', `Bearer ${driverToken}`)
      .send({
        note: 'Đã bàn giao cho RTG hạ bãi an toàn',
        sealNo: 'SEAL-VN-998811',
        containerCondition: 'Nguyên chì - Tốt',
        recipientName: 'Trần Văn Bãi (Yard Operator)',
      });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.trip.status).toBe('Container Delivered');
    expect(res.body.data.trip.sealNo).toBe('SEAL-VN-998811');
    expect(res.body.data.trip.containerCondition).toBe('Nguyên chì - Tốt');
    expect(res.body.data.trip.recipientName).toBe('Trần Văn Bãi (Yard Operator)');
    expect(res.body.data.trip.nextStep).toBe('Gate Out Completed');
  });

  it('Step 7: Driver confirms Gate Out (Container Delivered → Gate Out Completed)', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/gate-out`)
      .set('Authorization', `Bearer ${driverToken}`)
      .send({ note: 'Đã rời cổng Gate B1 ra ngoài cảng' });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.trip.status).toBe('Gate Out Completed');
    expect(res.body.data.trip.nextStep).toBe('Vehicle Returned');
  });

  it('Step 8: Driver returns vehicle and completes trip (Gate Out Completed → Completed)', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/return-vehicle`)
      .set('Authorization', `Bearer ${driverToken}`)
      .send({ note: 'Đã trả xe đầu kéo về bãi đỗ xe' });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.trip.status).toBe('Completed');
    expect(res.body.data.trip.nextStep).toBeNull();
  });

  it('Verification: Trip history records all steps and timestamps', async () => {
    const res = await request(app)
      .get(`/api/driver/trips/${tripId}`)
      .set('Authorization', `Bearer ${driverToken}`);

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);

    const trip = res.body.data.trip;
    expect(trip.history).toBeDefined();
    expect(trip.history.length).toBeGreaterThanOrEqual(8);

    const recordedStatuses = trip.history.map(h => h.toStatus);
    expect(recordedStatuses).toContain('Acknowledged');
    expect(recordedStatuses).toContain('Vehicle Picked Up');
    expect(recordedStatuses).toContain('Container Picked Up');
    expect(recordedStatuses).toContain('Gate In');
    expect(recordedStatuses).toContain('In Transit');
    expect(recordedStatuses).toContain('Arrived at Yard');
    expect(recordedStatuses).toContain('Container Delivered');
    expect(recordedStatuses).toContain('Gate Out Completed');
    expect(recordedStatuses).toContain('Vehicle Returned');
    expect(recordedStatuses).toContain('Completed');
  });

  it('Rule Check: Cannot perform further actions when trip is Completed', async () => {
    const res = await request(app)
      .patch(`/api/driver/trips/${tripId}/acknowledge`)
      .set('Authorization', `Bearer ${driverToken}`);

    expect(res.status).toBe(400);
    expect(res.body.success).toBe(false);
    expect(res.body.message).toContain('Chuyến đã hoàn thành');
  });
});

'use strict';

const request = require('supertest');
const app = require('../src/app');
const { TransportTrip, TransportTripHistory } = require('../src/models/TransportTrip');
const { generateToken } = require('../src/utils/jwt');
const { syncDB } = require('../src/config/database');
const { User } = require('../src/models/User');

describe('Driver Transport Orders API (Xem Lệnh Vận chuyển)', () => {
  const driverId = '22222222-3333-4444-5555-666666666666';
  const otherDriverId = '88888888-7777-6666-5555-444444444444';
  const emptyDriverId = '33333333-4444-5555-6666-777777777777';

  let driverToken;
  let otherDriverToken;
  let emptyDriverToken;

  let assignedTrip;
  let inProgressTrip;
  let completedTrip;
  let otherDriverTrip;

  beforeAll(async () => {
    await syncDB({ alter: true });

    await User.findOrCreate({
      where: { id: driverId },
      defaults: {
        username: 'driver_orders_test',
        password: 'password123',
        email: 'driver_orders_test@example.com',
        role: 'Driver',
      },
    });

    await User.findOrCreate({
      where: { id: otherDriverId },
      defaults: {
        username: 'other_driver_orders_test',
        password: 'password123',
        email: 'other_driver_orders_test@example.com',
        role: 'Driver',
      },
    });

    await User.findOrCreate({
      where: { id: emptyDriverId },
      defaults: {
        username: 'empty_driver_orders_test',
        password: 'password123',
        email: 'empty_driver_orders_test@example.com',
        role: 'Driver',
      },
    });

    driverToken = generateToken({ id: driverId, username: 'driver_orders_test', role: 'Driver' });
    otherDriverToken = generateToken({ id: otherDriverId, username: 'other_driver_orders_test', role: 'Driver' });
    emptyDriverToken = generateToken({ id: emptyDriverId, username: 'empty_driver_orders_test', role: 'Driver' });

    // Seed test trips for driver
    assignedTrip = await TransportTrip.create({
      trip_code: `TEST-ORD-${Date.now()}-1`,
      driver_id: driverId,
      booking_code: 'BK-IMPORT-101',
      booking_type: 'Import',
      truck_plate: '43C-111.22',
      container_no: 'CONT-ORD-001',
      container_type: '40HC',
      cargo_type: 'Điện tử',
      gate_in_code: 'GATE-A1',
      yard_block: 'Block A',
      yard_slot: 'A01-01',
      status: 'Assigned',
      appointment_start: new Date(Date.now() + 3600000),
      appointment_end: new Date(Date.now() + 7200000),
    });

    inProgressTrip = await TransportTrip.create({
      trip_code: `TEST-ORD-${Date.now()}-2`,
      driver_id: driverId,
      booking_code: 'BK-EXPORT-202',
      booking_type: 'Export',
      truck_plate: '43C-222.33',
      container_no: 'CONT-ORD-002',
      container_type: '20GP',
      cargo_type: 'Nông sản',
      gate_in_code: 'GATE-B2',
      yard_block: 'Block B',
      yard_slot: 'B02-04',
      status: 'In Transit',
      appointment_start: new Date(Date.now() - 3600000),
      appointment_end: new Date(Date.now() + 3600000),
    });

    completedTrip = await TransportTrip.create({
      trip_code: `TEST-ORD-${Date.now()}-3`,
      driver_id: driverId,
      booking_code: 'BK-STORAGE-303',
      booking_type: 'Storage',
      truck_plate: '43C-333.44',
      container_no: 'CONT-ORD-003',
      container_type: '40GP',
      cargo_type: 'Hàng tiêu dùng',
      yard_block: 'Block C',
      yard_slot: 'C03-01',
      status: 'Completed',
    });

    // Seed trip for other driver
    otherDriverTrip = await TransportTrip.create({
      trip_code: `TEST-ORD-${Date.now()}-99`,
      driver_id: otherDriverId,
      container_no: 'CONT-OTHER-999',
      truck_plate: '43C-999.99',
      status: 'Assigned',
    });
  });

  afterAll(async () => {
    if (assignedTrip) await assignedTrip.destroy();
    if (inProgressTrip) await inProgressTrip.destroy();
    if (completedTrip) await completedTrip.destroy();
    if (otherDriverTrip) await otherDriverTrip.destroy();
  });

  describe('GET /api/driver/transport-orders', () => {
    it('TC-TO-01: returns 401 when not authenticated', async () => {
      const res = await request(app).get('/api/driver/transport-orders');
      expect(res.status).toBe(401);
    });

    it('TC-TO-02: only returns orders assigned to logged in driver (isolation check)', async () => {
      const res = await request(app)
        .get('/api/driver/transport-orders')
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.orders).toBeDefined();

      const orderCodes = res.body.data.orders.map(o => o.orderCode);
      expect(orderCodes).toContain(assignedTrip.trip_code);
      expect(orderCodes).toContain(inProgressTrip.trip_code);
      expect(orderCodes).toContain(completedTrip.trip_code);
      expect(orderCodes).not.toContain(otherDriverTrip.trip_code);
    });

    it('TC-TO-03: filters by status=ASSIGNED properly', async () => {
      const res = await request(app)
        .get('/api/driver/transport-orders?status=ASSIGNED')
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.orders.length).toBeGreaterThan(0);
      res.body.data.orders.forEach(order => {
        expect(order.status).toBe('Assigned');
        expect(order.statusGroup).toBe('ASSIGNED');
      });
    });

    it('TC-TO-04: filters by status=IN_PROGRESS properly', async () => {
      const res = await request(app)
        .get('/api/driver/transport-orders?status=IN_PROGRESS')
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.orders.length).toBeGreaterThan(0);
      res.body.data.orders.forEach(order => {
        expect(order.statusGroup).toBe('IN_PROGRESS');
      });
    });

    it('TC-TO-05: filters by status=COMPLETED properly', async () => {
      const res = await request(app)
        .get('/api/driver/transport-orders?status=COMPLETED')
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.orders.length).toBeGreaterThan(0);
      res.body.data.orders.forEach(order => {
        expect(order.status).toBe('Completed');
        expect(order.statusGroup).toBe('COMPLETED');
      });
    });

    it('TC-TO-06: returns empty array and total=0 when driver has no assigned orders', async () => {
      const res = await request(app)
        .get('/api/driver/transport-orders')
        .set('Authorization', `Bearer ${emptyDriverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.orders).toEqual([]);
      expect(res.body.data.total).toBe(0);
      expect(res.body.data.totalPages).toBe(0);
    });
  });

  describe('GET /api/driver/transport-orders/:id', () => {
    it('TC-TO-07: returns full details and mapping for Container, Booking, Vehicle, Yard, Schedule', async () => {
      const res = await request(app)
        .get(`/api/driver/transport-orders/${assignedTrip.id}`)
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);

      const order = res.body.data.order;
      expect(order).toBeDefined();
      expect(order.id).toBe(assignedTrip.id);
      expect(order.orderCode).toBe(assignedTrip.trip_code);
      expect(order.status).toBe('Assigned');
      expect(order.statusGroup).toBe('ASSIGNED');
      expect(order.nextStep).toBe('Acknowledged');

      // Check mapping: Driver - Vehicle - Container - Booking - Yard - Schedule
      expect(order.mapping).toBeDefined();
      expect(order.mapping.driver.id).toBe(driverId);
      expect(order.mapping.vehicle.truckPlate).toBe('43C-111.22');
      expect(order.mapping.container.containerNo).toBe('CONT-ORD-001');
      expect(order.mapping.container.containerType).toBe('40HC');
      expect(order.mapping.container.cargoType).toBe('Điện tử');
      expect(order.mapping.booking.bookingCode).toBe('BK-IMPORT-101');
      expect(order.mapping.booking.bookingType).toBe('Import');
      expect(order.mapping.yard.block).toBe('Block A');
      expect(order.mapping.yard.slot).toBe('A01-01');
      expect(order.mapping.yard.name).toBeDefined();
      expect(order.mapping.yard.address).toContain('Đà Nẵng');
      expect(order.mapping.yard.coordinates).toBeDefined();
      expect(order.mapping.schedule.appointmentStart).toBeDefined();
    });

    it('TC-TO-08: returns 404 when attempting to access another driver\'s order', async () => {
      const res = await request(app)
        .get(`/api/driver/transport-orders/${otherDriverTrip.id}`)
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(404);
      expect(res.body.success).toBe(false);
    });
  });

  describe('GET /api/driver/trips (Backwards Compatibility & Status Groups)', () => {
    it('TC-TO-09: /api/driver/trips also supports statusGroup filter and returns mapping', async () => {
      const res = await request(app)
        .get('/api/driver/trips?status=IN_PROGRESS')
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.trips.length).toBeGreaterThan(0);
      expect(res.body.data.trips[0].mapping).toBeDefined();
      expect(res.body.data.trips[0].statusGroup).toBe('IN_PROGRESS');
    });
  });
});

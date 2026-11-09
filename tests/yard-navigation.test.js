'use strict';

const request = require('supertest');
const app = require('../src/app');
const { TransportTrip } = require('../src/models/TransportTrip');
const { generateToken } = require('../src/utils/jwt');

const { syncDB } = require('../src/config/database');
const { User } = require('../src/models/User');

describe('Driver Yard Navigation API', () => {
  const driverId = '11111111-2222-3333-4444-555555555555';
  const otherDriverId = '99999999-8888-7777-6666-555555555555';
  let driverToken;
  let otherDriverToken;

  beforeAll(async () => {
    await syncDB({ alter: true });
    await User.findOrCreate({
      where: { id: driverId },
      defaults: {
        username: 'testdriver_nxp138',
        password: 'password123',
        email: 'testdriver_nxp138@example.com',
        role: 'Driver',
      },
    });
    await User.findOrCreate({
      where: { id: otherDriverId },
      defaults: {
        username: 'otherdriver_nxp138',
        password: 'password123',
        email: 'otherdriver_nxp138@example.com',
        role: 'Driver',
      },
    });
    driverToken = generateToken({ id: driverId, username: 'testdriver_nxp138', role: 'Driver' });
    otherDriverToken = generateToken({ id: otherDriverId, username: 'otherdriver_nxp138', role: 'Driver' });
  });

  describe('GET /api/driver/trips/:id/yard-navigation', () => {
    it('returns 401 when not authenticated', async () => {
      const res = await request(app).get('/api/driver/trips/00000000-0000-0000-0000-000000000000/yard-navigation');
      expect(res.status).toBe(401);
    });

    it('returns 404 when trip does not exist or belongs to another driver', async () => {
      let trip;
      try {
        trip = await TransportTrip.create({
          trip_code: `TEST-TRIP-${Date.now()}-1`,
          driver_id: driverId,
          yard_block: 'Block A',
          yard_slot: 'A01-01',
          status: 'Assigned',
        });
      } catch (err) {
        console.error('DEBUG SEQUELIZE ERROR:', err.message, err.parent);
        throw err;
      }

      const res = await request(app)
        .get(`/api/driver/trips/${trip.id}/yard-navigation`)
        .set('Authorization', `Bearer ${otherDriverToken}`);

      expect(res.status).toBe(404);
      await trip.destroy();
    });

    it('blocks navigation when trip is NOT In Transit (status = Assigned)', async () => {
      const trip = await TransportTrip.create({
        trip_code: `TEST-TRIP-${Date.now()}-2`,
        driver_id: driverId,
        yard_block: 'Block A',
        yard_slot: 'A02-01',
        status: 'Assigned',
      });

      const res = await request(app)
        .get(`/api/driver/trips/${trip.id}/yard-navigation`)
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.canNavigate).toBe(false);
      expect(res.body.data.isNavigable).toBe(false);
      expect(res.body.data.status).toBe('Assigned');
      expect(res.body.data.navigationBlockReason).toContain('Chưa nhận container');
      expect(res.body.data.yard.name).toBeDefined();
      expect(res.body.data.yard.address).toContain('Đà Nẵng');
      expect(res.body.data.yard.coordinates.lat).toBeCloseTo(16.1265);

      await trip.destroy();
    });

    it('blocks navigation when trip is Container Picked Up (not yet gate in)', async () => {
      const trip = await TransportTrip.create({
        trip_code: `TEST-TRIP-${Date.now()}-3`,
        driver_id: driverId,
        yard_block: 'Block B',
        yard_slot: 'B01-05',
        status: 'Container Picked Up',
      });

      const res = await request(app)
        .get(`/api/driver/trips/${trip.id}/yard-navigation`)
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.data.canNavigate).toBe(false);
      expect(res.body.data.navigationBlockReason).toContain('Gate In');

      await trip.destroy();
    });

    it('allows navigation when trip IS In Transit and yard info is valid', async () => {
      const trip = await TransportTrip.create({
        trip_code: `TEST-TRIP-${Date.now()}-4`,
        driver_id: driverId,
        container_no: 'CONT-NAV-999',
        truck_plate: '43C-999.88',
        yard_block: 'Block A',
        yard_slot: 'A05-02',
        yard_instructions: 'Vào cổng Gate A1 -> Đi thẳng 200m -> Rẽ vào Block A',
        status: 'In Transit',
      });

      const res = await request(app)
        .get(`/api/driver/trips/${trip.id}/yard-navigation`)
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.data.canNavigate).toBe(true);
      expect(res.body.data.isNavigable).toBe(true);
      expect(res.body.data.navigationBlockReason).toBeNull();
      expect(res.body.data.isYardValid).toBe(true);
      expect(res.body.data.yard.block).toBe('Block A');
      expect(res.body.data.yard.slot).toBe('A05-02');
      expect(res.body.data.yard.name).toContain('Tiên Sa');
      expect(res.body.data.yard.address).toContain('Đà Nẵng');
      expect(res.body.data.yard.coordinates.lat).toBeDefined();
      expect(res.body.data.yard.coordinates.lng).toBeDefined();
      expect(res.body.data.yard.mapUrl).toContain('google.com/maps/dir');

      await trip.destroy();
    });

    it('detects invalid yard info when block is missing', async () => {
      const trip = await TransportTrip.create({
        trip_code: `TEST-TRIP-${Date.now()}-5`,
        driver_id: driverId,
        yard_block: null,
        status: 'In Transit',
      });

      const res = await request(app)
        .get(`/api/driver/trips/${trip.id}/yard-navigation`)
        .set('Authorization', `Bearer ${driverToken}`);

      expect(res.status).toBe(200);
      expect(res.body.data.canNavigate).toBe(false);
      expect(res.body.data.isYardValid).toBe(false);
      expect(res.body.data.validationMessage).toContain('chưa được phân công hoặc không đầy đủ');

      await trip.destroy();
    });
  });
});

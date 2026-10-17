'use strict';

const http = require('http');
const ioClient = require('socket.io-client');
const app = require('../src/app');
const { initSocket } = require('../src/config/socket');
const { TransportTrip } = require('../src/models/TransportTrip');
const { User } = require('../src/models/User');
const { generateToken } = require('../src/utils/jwt');
const { syncDB } = require('../src/config/database');
const tripsService = require('../src/modules/trips/trips.service');

describe('Driver & Dispatcher Realtime Socket API', () => {
  let server;
  let serverUrl;
  let io;

  const driverId = '55555555-4444-3333-2222-111111111111';
  const otherDriverId = '66666666-5555-4444-3333-222222222222';
  const dispatcherId = '77777777-6666-5555-4444-333333333333';

  let driverToken;
  let otherDriverToken;
  let dispatcherToken;

  let testTrip;

  beforeAll(async () => {
    await syncDB({ alter: true });

    // Seed users
    await User.findOrCreate({
      where: { id: driverId },
      defaults: {
        username: 'rt_driver_test',
        password: 'password123',
        email: 'rt_driver@example.com',
        role: 'Driver',
      },
    });

    await User.findOrCreate({
      where: { id: otherDriverId },
      defaults: {
        username: 'rt_other_driver_test',
        password: 'password123',
        email: 'rt_other_driver@example.com',
        role: 'Driver',
      },
    });

    await User.findOrCreate({
      where: { id: dispatcherId },
      defaults: {
        username: 'rt_dispatcher_test',
        password: 'password123',
        email: 'rt_dispatcher@example.com',
        role: 'Dispatcher',
      },
    });

    driverToken = generateToken({ id: driverId, username: 'rt_driver_test', role: 'Driver' });
    otherDriverToken = generateToken({ id: otherDriverId, username: 'rt_other_driver_test', role: 'Driver' });
    dispatcherToken = generateToken({ id: dispatcherId, username: 'rt_dispatcher_test', role: 'Dispatcher' });

    // Start test HTTP server with socket
    server = http.createServer(app);
    io = initSocket(server);

    await new Promise((resolve) => {
      server.listen(0, () => {
        const port = server.address().port;
        serverUrl = `http://localhost:${port}`;
        resolve();
      });
    });
  });

  afterAll(async () => {
    if (testTrip?.id) {
      await TransportTrip.destroy({ where: { id: testTrip.id } }).catch(() => {});
    }
    if (io) {
      await new Promise((resolve) => io.close(resolve));
    }
    if (server) {
      await new Promise((resolve) => server.close(resolve));
    }
  });

  it('TC-RT-01: rejects socket connection without token', (done) => {
    const socket = ioClient(serverUrl, {
      transports: ['websocket'],
      reconnection: false,
    });

    socket.on('connect_error', (err) => {
      expect(err.message).toContain('Yêu cầu xác thực');
      socket.disconnect();
      done();
    });

    socket.on('connect', () => {
      socket.disconnect();
      done(new Error('Should not have connected without token'));
    });
  });

  it('TC-RT-02: rejects socket connection with invalid token', (done) => {
    const socket = ioClient(serverUrl, {
      auth: { token: 'invalid_jwt_token_123' },
      transports: ['websocket'],
      reconnection: false,
    });

    socket.on('connect_error', (err) => {
      expect(err.message).toContain('Token xác thực không hợp lệ');
      socket.disconnect();
      done();
    });
  });

  it('TC-RT-03: successfully connects with valid driver token', (done) => {
    const socket = ioClient(serverUrl, {
      auth: { token: driverToken },
      transports: ['websocket'],
    });

    socket.on('connect', () => {
      expect(socket.connected).toBe(true);
      socket.disconnect();
      done();
    });
  });

  it('TC-RT-04: receives trip:assigned event when new trip is created for driver', (done) => {
    const socket = ioClient(serverUrl, {
      auth: { token: driverToken },
      transports: ['websocket'],
    });

    socket.on('connect', async () => {
      socket.on('trip:assigned', (data) => {
        try {
          expect(data.trip).toBeDefined();
          expect(data.trip.driverId).toBe(driverId);
          expect(data.status).toBe('Assigned');
          expect(data.trip.containerNo).toBe('CONT-RT-001');
          socket.disconnect();
          done();
        } catch (error) {
          socket.disconnect();
          done(error);
        }
      });

      // Tạo chuyến mới cho driverId
      testTrip = await tripsService.createTrip({
        driverId,
        containerNo: 'CONT-RT-001',
        truckPlate: '43C-888.99',
        yardBlock: 'Block A',
        yardSlot: 'A02-01',
        bookingCode: 'BK-RT-001',
        bookingType: 'Import',
        assignedBy: dispatcherId,
      });
    });
  });

  it('TC-RT-05: does NOT send trip:assigned to a different driver', (done) => {
    const otherSocket = ioClient(serverUrl, {
      auth: { token: otherDriverToken },
      transports: ['websocket'],
    });

    let receivedOnOther = false;

    otherSocket.on('connect', async () => {
      otherSocket.on('trip:assigned', () => {
        receivedOnOther = true;
      });

      // Tạo chuyến cho driverId đầu tiên
      const anotherTrip = await tripsService.createTrip({
        driverId,
        containerNo: 'CONT-RT-002',
        truckPlate: '43C-777.88',
        yardBlock: 'Block B',
        assignedBy: dispatcherId,
      });

      // Đợi 200ms để đảm bảo không nhận sự kiện
      setTimeout(async () => {
        await TransportTrip.destroy({ where: { id: anotherTrip.id } }).catch(() => {});
        otherSocket.disconnect();
        expect(receivedOnOther).toBe(false);
        done();
      }, 200);
    });
  });

  it('TC-RT-06: receives trip:status_updated when driver advances status', (done) => {
    const socket = ioClient(serverUrl, {
      auth: { token: driverToken },
      transports: ['websocket'],
    });

    socket.on('connect', async () => {
      socket.on('trip:status_updated', (data) => {
        try {
          expect(data.tripId).toBe(testTrip.id);
          expect(data.status).toBe('Acknowledged');
          expect(data.nextStep).toBe('Vehicle Picked Up');
          socket.disconnect();
          done();
        } catch (error) {
          socket.disconnect();
          done(error);
        }
      });

      // Driver xác nhận nhận chuyến (Assigned -> Acknowledged)
      await tripsService.acknowledgeTrip(testTrip.id, driverId);
    });
  });

  it('TC-RT-07: dispatcher receives dispatcher.status.updated event on status change', (done) => {
    const dispatcherSocket = ioClient(serverUrl, {
      auth: { token: dispatcherToken },
      transports: ['websocket'],
    });

    dispatcherSocket.on('connect', async () => {
      dispatcherSocket.on('dispatcher.status.updated', (data) => {
        try {
          expect(data.domain).toBe('driver');
          expect(data.entityId).toBe(testTrip.id);
          expect(data.status).toBe('Vehicle Picked Up');
          dispatcherSocket.disconnect();
          done();
        } catch (error) {
          dispatcherSocket.disconnect();
          done(error);
        }
      });

      // Chuyển sang Vehicle Picked Up
      await tripsService.confirmVehiclePickup(testTrip.id, driverId, 'Xe đã nhận tại bãi');
    });
  });
});

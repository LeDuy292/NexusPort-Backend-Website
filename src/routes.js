'use strict';

const { Router } = require('express');
const authRoutes = require('./modules/auth/auth.routes');
const protectedRoutes = require('./modules/protected/protected.routes');
const usersRoutes = require('./modules/users/users.routes');
const tripsRoutes = require('./modules/trips/trips.routes');
const yardRoutes = require('./modules/yard/yard.routes');
const adminRoutes = require('./modules/admin/admin.routes');
const driverBookingRoutes = require('./modules/driver-bookings/driver-bookings.routes');
const dispatchRoutes = require('./modules/booking-dispatch/dispatch.routes');
const gateIotRoutes = require('./modules/gate-iot/gate-iot.routes');
const shippingLineRoutes = require('./modules/shipping-line/shippingLine.routes');
const importDocumentsRoutes = require('./modules/import-documents/importDocuments.routes');
const blImportRoutes = require('./modules/bl-import/blImport.routes');
const cargoNotificationRoutes = require('./modules/cargo-notifications/cargoNotification.routes');
const { nodeEnv } = require('./config/env');

const systemRoutes = Router();
const healthHandler = (_req, res) => {
  res.status(200).json({
    success: true,
    message: 'NexusPort API đang hoạt động.',
    timestamp: new Date().toISOString(),
    environment: nodeEnv,
  });
};
systemRoutes.get('/health', healthHandler);
systemRoutes.get('/api/health', healthHandler);

const { sequelize } = require('./config/database');

// Persistent storage for yard reservations across all machines via PostgreSQL
const yardReservationsRoutes = Router();
let reservationsStore = [];

yardReservationsRoutes.get('/', async (_req, res) => {
  try {
    const [rows] = await sequelize.query(`
      SELECT data FROM persistent_yard_reservations ORDER BY updated_at DESC;
    `);
    if (rows && rows.length > 0) {
      const list = rows.map(r => r.data);
      reservationsStore = list;
      return res.json(list);
    }
  } catch (err) {
    console.warn('[YardReservations] DB read warning:', err.message);
  }
  res.json(reservationsStore);
});

yardReservationsRoutes.post('/', async (req, res) => {
  const item = req.body;
  if (item) {
    const contId = (item.containerId || item.containerNo || item.booking?.containerId || '').trim().toUpperCase();
    const existingIdx = reservationsStore.findIndex(r => {
      const rCont = (r.containerId || r.containerNo || r.booking?.containerId || '').trim().toUpperCase();
      return contId && rCont === contId;
    });
    if (existingIdx >= 0) {
      reservationsStore[existingIdx] = { ...reservationsStore[existingIdx], ...item, updatedAt: new Date().toISOString() };
    } else {
      reservationsStore.push({ ...item, createdAt: new Date().toISOString() });
    }

    try {
      const id = contId || item.posId || `RES-${Date.now()}`;
      const blockCode = item.blockCode || item.block || 'A01';
      const bay = parseInt(item.bay, 10) || 2;
      const rowNo = parseInt(item.row, 10) || 1;
      const tier = parseInt(item.tier, 10) || 1;
      const size = parseInt(item.size, 10) || 40;
      const bookingCode = item.bookingCode || item.booking?.id || '';

      await sequelize.query(`
        INSERT INTO persistent_yard_reservations (id, container_no, booking_code, block_code, bay, row_no, tier, size, status, data, updated_at)
        VALUES (:id, :contId, :bookingCode, :blockCode, :bay, :rowNo, :tier, :size, 'reserved', :data, NOW())
        ON CONFLICT (id) DO UPDATE SET
          container_no = EXCLUDED.container_no,
          booking_code = EXCLUDED.booking_code,
          block_code = EXCLUDED.block_code,
          bay = EXCLUDED.bay,
          row_no = EXCLUDED.row_no,
          tier = EXCLUDED.tier,
          size = EXCLUDED.size,
          status = EXCLUDED.status,
          data = EXCLUDED.data,
          updated_at = NOW();
      `, {
        replacements: {
          id,
          contId,
          bookingCode,
          blockCode,
          bay,
          rowNo,
          tier,
          size,
          data: JSON.stringify(item),
        }
      });
    } catch (err) {
      console.warn('[YardReservations] DB write warning:', err.message);
    }
  }
  res.status(201).json(item);
});

yardReservationsRoutes.delete('/:posId', async (req, res) => {
  reservationsStore = reservationsStore.filter(r => r.posId !== req.params.posId);
  try {
    await sequelize.query(`DELETE FROM persistent_yard_reservations WHERE id = :id OR data->>'posId' = :id`, {
      replacements: { id: req.params.posId }
    });
  } catch (err) {
    console.warn('[YardReservations] DB delete warning:', err.message);
  }
  res.json({ success: true });
});

yardReservationsRoutes.delete('/container/:containerId', async (req, res) => {
  const key = (req.params.containerId || '').trim().toUpperCase();
  reservationsStore = reservationsStore.filter(r => {
    const rCont = (r.containerId || r.containerNo || r.booking?.containerId || '').trim().toUpperCase();
    return rCont !== key;
  });
  try {
    await sequelize.query(`DELETE FROM persistent_yard_reservations WHERE container_no = :key OR id = :key`, {
      replacements: { key }
    });
  } catch (err) {
    console.warn('[YardReservations] DB delete warning:', err.message);
  }
  res.json({ success: true });
});

yardReservationsRoutes.delete('/', async (_req, res) => {
  reservationsStore = [];
  try {
    await sequelize.query(`DELETE FROM persistent_yard_reservations`);
  } catch (err) {
    console.warn('[YardReservations] DB delete warning:', err.message);
  }
  res.json({ success: true });
});

/** This catalog is the single source used by Express and OpenAPI discovery. */
const mountedRouters = [
  { prefix: '/', tag: 'System', secured: false, router: systemRoutes },
  { prefix: '/api/yard-reservations', tag: 'Yard Reservations', secured: false, router: yardReservationsRoutes },
  { prefix: '/yard-reservations', tag: 'Yard Reservations Direct', secured: false, router: yardReservationsRoutes },
  { prefix: '/api/auth', tag: 'Auth', secured: false, router: authRoutes },
  { prefix: '/auth', tag: 'Auth Direct', secured: false, router: authRoutes },
  { prefix: '/api/gate', tag: 'Gate IoT', secured: false, router: gateIotRoutes },
  { prefix: '/gate', tag: 'Gate IoT Direct', secured: false, router: gateIotRoutes },
  { prefix: '/api/shipping-line', tag: 'Shipping Line', secured: false, router: shippingLineRoutes },
  { prefix: '/shipping-line', tag: 'Shipping Line Direct', secured: false, router: shippingLineRoutes },
  { prefix: '/api/import-documents', tag: 'Import Documents', secured: false, router: importDocumentsRoutes },
  { prefix: '/api/bl-imports', tag: 'BL Imports', secured: false, router: blImportRoutes },
  { prefix: '/import-documents', tag: 'Import Documents Direct', secured: false, router: importDocumentsRoutes },
  { prefix: '/bl-imports', tag: 'BL Imports Direct', secured: false, router: blImportRoutes },
  { prefix: '/api/cargo-notifications', tag: 'Cargo Notifications', secured: false, router: cargoNotificationRoutes },
  { prefix: '/cargo-notifications', tag: 'Cargo Notifications Direct', secured: false, router: cargoNotificationRoutes },
  { prefix: '/api/driver-bookings', tag: 'Driver Bookings', secured: true, router: driverBookingRoutes },
  { prefix: '/driver-bookings', tag: 'Driver Bookings Direct', secured: true, router: driverBookingRoutes },
  { prefix: '/api/booking-dispatch', tag: 'Booking Dispatch', secured: true, router: dispatchRoutes },
  { prefix: '/booking-dispatch', tag: 'Booking Dispatch Direct', secured: true, router: dispatchRoutes },
  { prefix: '/api/users', tag: 'Users', secured: true, router: usersRoutes },
  { prefix: '/users', tag: 'Users Direct', secured: true, router: usersRoutes },
  { prefix: '/api/yard', tag: 'Yard Operations', secured: true, router: yardRoutes },
  { prefix: '/yard', tag: 'Yard Operations Direct', secured: true, router: yardRoutes },
  { prefix: '/api/admin', tag: 'Administrator', secured: true, router: adminRoutes },
  { prefix: '/admin', tag: 'Administrator Direct', secured: true, router: adminRoutes },
  { prefix: '/api', tag: 'Transport Trips', secured: true, router: tripsRoutes },
  { prefix: '/api', tag: 'RBAC Demo', secured: true, router: protectedRoutes },
];

module.exports = { mountedRouters };

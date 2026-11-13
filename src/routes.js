'use strict';

const { Router } = require('express');
const authRoutes = require('./modules/auth/auth.routes');
const protectedRoutes = require('./modules/protected/protected.routes');
const usersRoutes = require('./modules/users/users.routes');
const tripsRoutes = require('./modules/trips/trips.routes');
const yardRoutes = require('./modules/yard/yard.routes');
const driverBookingRoutes = require('./modules/driver-bookings/driver-bookings.routes');
const dispatchRoutes = require('./modules/booking-dispatch/dispatch.routes');
const gateIotRoutes = require('./modules/gate-iot/gate-iot.routes');
const shippingLineRoutes = require('./modules/shipping-line/shippingLine.routes');
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

/** This catalog is the single source used by Express and OpenAPI discovery. */
const mountedRouters = [
  { prefix: '/', tag: 'System', secured: false, router: systemRoutes },
  { prefix: '/api/auth', tag: 'Auth', secured: false, router: authRoutes },
  { prefix: '/auth', tag: 'Auth Direct', secured: false, router: authRoutes },
  { prefix: '/api/gate', tag: 'Gate IoT', secured: false, router: gateIotRoutes },
  { prefix: '/gate', tag: 'Gate IoT Direct', secured: false, router: gateIotRoutes },
  { prefix: '/api/shipping-line', tag: 'Shipping Line', secured: false, router: shippingLineRoutes },
  { prefix: '/shipping-line', tag: 'Shipping Line Direct', secured: false, router: shippingLineRoutes },
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
  { prefix: '/api', tag: 'Transport Trips', secured: true, router: tripsRoutes },
  { prefix: '/api', tag: 'RBAC Demo', secured: true, router: protectedRoutes },
];

module.exports = { mountedRouters };

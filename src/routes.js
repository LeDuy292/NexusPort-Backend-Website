'use strict';

const { Router } = require('express');
const authRoutes = require('./modules/auth/auth.routes');
const protectedRoutes = require('./modules/protected/protected.routes');
const usersRoutes = require('./modules/users/users.routes');
const tripsRoutes = require('./modules/trips/trips.routes');
const yardRoutes = require('./modules/yard/yard.routes');
const driverBookingRoutes = require('./modules/driver-bookings/driver-bookings.routes');
const dispatchRoutes = require('./modules/booking-dispatch/dispatch.routes');
const { nodeEnv } = require('./config/env');

const systemRoutes = Router();
systemRoutes.get('/api/health', (_req, res) => {
  res.status(200).json({
    success: true,
    message: 'NexusPort API đang hoạt động.',
    timestamp: new Date().toISOString(),
    environment: nodeEnv,
  });
});

/** This catalog is the single source used by Express and OpenAPI discovery. */
const mountedRouters = [
  { prefix: '/', tag: 'System', secured: false, router: systemRoutes },
  { prefix: '/api/auth', tag: 'Auth', secured: false, router: authRoutes },
  { prefix: '/api/driver-bookings', tag: 'Driver Bookings', secured: true, router: driverBookingRoutes },
  { prefix: '/api/booking-dispatch', tag: 'Booking Dispatch', secured: true, router: dispatchRoutes },
  { prefix: '/api/users', tag: 'Users', secured: true, router: usersRoutes },
  { prefix: '/api/yard', tag: 'Yard Operations', secured: true, router: yardRoutes },
  { prefix: '/api', tag: 'Transport Trips', secured: true, router: tripsRoutes },
  { prefix: '/api', tag: 'RBAC Demo', secured: true, router: protectedRoutes },
];

module.exports = { mountedRouters };

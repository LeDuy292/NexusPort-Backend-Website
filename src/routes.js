'use strict';

const { Router } = require('express');
const authRoutes = require('./modules/auth/auth.routes');
const protectedRoutes = require('./modules/protected/protected.routes');
const usersRoutes = require('./modules/users/users.routes');
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
  { prefix: '/api/users', tag: 'Users', secured: true, router: usersRoutes },
  { prefix: '/api', tag: 'RBAC Demo', secured: true, router: protectedRoutes },
];

module.exports = { mountedRouters };

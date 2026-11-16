'use strict';

const express = require('express');
const path = require('path');
const cors = require('cors');
const helmet = require('helmet');
const morgan = require('morgan');
const { createProxyMiddleware } = require('http-proxy-middleware');
const swaggerUi = require('swagger-ui-express');
const { swaggerSpec } = require('./docs/swagger');
const { errorHandler } = require('./middlewares/errorHandler');
const { mountedRouters } = require('./routes');
const { nodeEnv } = require('./config/env');

const app = express();

// ─── Security & Utility Middlewares ─────────────────────────────────────────
app.use(
  helmet({
    crossOriginResourcePolicy: { policy: 'cross-origin' },
  })
);

app.use(
  cors({
    origin: process.env.CORS_ORIGIN || '*',
    methods: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'],
    allowedHeaders: ['Content-Type', 'Authorization', 'Cache-Control', 'Pragma', 'Expires', 'X-Requested-With'],
  })
);

// ─── Reverse Proxies (Chuyển tiếp /api/v1 sang C# Core & /node-api sang Node Core) ──
const normalizeTargetUrl = (url, fallback) => {
  const target = (url || fallback || '').trim();
  if (!target) return fallback;
  if (!target.startsWith('http://') && !target.startsWith('https://')) {
    return `https://${target}`;
  }
  return target;
};

const csharpTarget = normalizeTargetUrl(
  process.env.CSHARP_CORE_URL || process.env.RAILWAY_SERVICE_CSHARP_CORE_URL,
  'https://csharp-core-production.up.railway.app'
);

const nodeCoreTarget = normalizeTargetUrl(
  process.env.NODE_CORE_URL || process.env.RAILWAY_SERVICE_NODE_CORE_URL,
  'https://node-core-production.up.railway.app'
);

const csharpProxy = createProxyMiddleware({
  target: csharpTarget,
  changeOrigin: true,
  pathRewrite: (path) => `/api/v1${path}`,
  logLevel: 'debug',
  on: {
    error: (err, req, res) => {
      console.error('[CSharp Proxy Error]', err);
    },
  },
});

const nodeProxy = createProxyMiddleware({
  target: nodeCoreTarget,
  changeOrigin: true,
  pathRewrite: (path) => `/api/v1${path}`,
  logLevel: 'debug',
  on: {
    error: (err, req, res) => {
      console.error('[NodeCore Proxy Error]', err);
    },
  },
});

app.use('/api/v1', csharpProxy);
app.use('/v1', csharpProxy);
app.use('/api/node-api', nodeProxy);
app.use('/node-api', nodeProxy);

app.use(express.json({ limit: '10mb' }));
app.use(express.urlencoded({ extended: true }));

// Serve uploaded files statically
app.use('/uploads', express.static(path.join(__dirname, '../uploads')));

if (nodeEnv !== 'test') {
  app.use(morgan('dev'));
}

// ─── Swagger API Docs ────────────────────────────────────────────────────────
app.get('/api-docs/openapi.json', (_req, res) => res.json(swaggerSpec));
app.use(
  '/api-docs',
  swaggerUi.serve,
  swaggerUi.setup(swaggerSpec, {
    customSiteTitle: 'NexusPort API Docs',
    swaggerOptions: {
      persistAuthorization: true,
    },
  })
);

// ─── API Routes ──────────────────────────────────────────────────────────────
for (const route of mountedRouters) app.use(route.prefix, route.router);

// ─── 404 Handler ─────────────────────────────────────────────────────────────
app.use((req, res) => {
  res.status(404).json({
    success: false,
    message: `Endpoint không tồn tại: ${req.method} ${req.originalUrl}`,
  });
});

// ─── Global Error Handler ────────────────────────────────────────────────────
app.use(errorHandler);

module.exports = app;

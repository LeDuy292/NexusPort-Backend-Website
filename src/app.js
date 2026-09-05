'use strict';

const express = require('express');
const cors = require('cors');
const helmet = require('helmet');
const morgan = require('morgan');
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
    allowedHeaders: ['Content-Type', 'Authorization'],
  })
);

app.use(express.json({ limit: '10mb' }));
app.use(express.urlencoded({ extended: true }));

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

import express, { Application } from 'express';
import cors from 'cors';
import helmet from 'helmet';
import swaggerUi from 'swagger-ui-express';
import routes from './routes';
import { errorHandler, requestLogger, authMiddleware } from './middleware/error.middleware';
import { nodeCoreOpenApi } from './openapi';

export const createApp = (): Application => {
  const app = express();

  // Security & standard middlewares
  app.use(
    helmet({
      crossOriginResourcePolicy: { policy: 'cross-origin' },
      contentSecurityPolicy: false,
      hsts: false,
    })
  );
  app.use(
    cors({
      origin: true,
      credentials: true,
      methods: ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'],
      allowedHeaders: ['Content-Type', 'Authorization', 'Cache-Control', 'Pragma', 'Expires', 'X-Requested-With'],
    })
  );
  // A reviewed Excel Manifest may contain thousands of cargo descriptions.
  // Keep the larger JSON allowance scoped to Manifest save routes.
  app.use(['/api/v1/shipping-line/manifests', '/api/shipping-line/manifests'], express.json({ limit: '32mb' }));
  app.use(express.json());
  app.use(express.urlencoded({ extended: true }));
  app.use(requestLogger);
  app.use(authMiddleware);

  // Machine-readable contract and a dedicated Swagger UI for the TypeScript service.
  app.get('/openapi.json', (_req, res) => res.json(nodeCoreOpenApi));
  app.use('/api-docs', swaggerUi.serve, swaggerUi.setup(nodeCoreOpenApi, {
    customSiteTitle: 'NexusPort TypeScript API Docs',
    swaggerOptions: { persistAuthorization: true },
  }));

  // Base API routes (mounted at /api/v1 and /api for full backward compatibility)
  app.use('/api/v1', routes);
  app.use('/api', routes);

  // Global error handling
  app.use(errorHandler);

  return app;
};

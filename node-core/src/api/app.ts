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
  app.use(helmet());
  app.use(cors());
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

  // Base API routes
  app.use('/api/v1', routes);

  // Global error handling
  app.use(errorHandler);

  return app;
};

import express, { Application } from 'express';
import cors from 'cors';
import helmet from 'helmet';
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

  // Machine-readable contract consumed by the aggregated Swagger UI on port 3001.
  app.get('/openapi.json', (_req, res) => res.json(nodeCoreOpenApi));

  // Base API routes
  app.use('/api/v1', routes);

  // Global error handling
  app.use(errorHandler);

  return app;
};

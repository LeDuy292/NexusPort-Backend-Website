import 'dotenv/config';
import { createServer } from 'http';
import { createApp } from './api/app';
import { DriverRealtimeGateway } from './realtime/driver-realtime.gateway';
import { logger } from './shared/utils/logger';

const port = Number(process.env.PORT || 4000);
const app = createApp();
const server = createServer(app);
const realtimeGateway = new DriverRealtimeGateway(server);

const startRealtime = async (): Promise<void> => {
  if (process.env.ENABLE_RABBITMQ === 'false') {
    logger.info('[Realtime] RabbitMQ message broker listener is disabled (ENABLE_RABBITMQ=false).');
    return;
  }
  try {
    await realtimeGateway.start();
  } catch (error: any) {
    const isConnRefused = error?.code === 'ECONNREFUSED' ||
      (Array.isArray(error?.errors) && error.errors.some((e: any) => e.code === 'ECONNREFUSED'));

    if (isConnRefused) {
      logger.warn('[Realtime] RabbitMQ broker is not running at localhost:5672. Realtime message consumer is paused. (Set ENABLE_RABBITMQ=false in .env to disable or start RabbitMQ)');
      setTimeout(() => void startRealtime(), 30000);
      return;
    }
    logger.error('[Realtime] Startup failed; retrying in 10 seconds:', error);
    setTimeout(() => void startRealtime(), 10000);
  }
};
void startRealtime();

server.listen(port, () => {
  logger.info(`🚀 NexusPort Node.js Core Service listening on port ${port}`);
  logger.info(`👉 Health check: http://localhost:${port}/api/v1/health`);
});

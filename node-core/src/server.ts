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
  try {
    await realtimeGateway.start();
  } catch (error) {
    logger.error('[Realtime] Startup failed; retrying in 5 seconds:', error);
    setTimeout(() => void startRealtime(), 5000);
  }
};
void startRealtime();

server.listen(port, () => {
  logger.info(`🚀 NexusPort Node.js Core Service listening on port ${port}`);
  logger.info(`👉 Health check: http://localhost:${port}/api/v1/health`);
});

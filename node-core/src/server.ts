import 'dotenv/config';
import { createServer } from 'http';
import { createApp } from './api/app';
import { DriverRealtimeGateway } from './realtime/driver-realtime.gateway';
import { logger } from './shared/utils/logger';
import { RabbitMQClient } from './infrastructure/clients/rabbitmq';
import pool from './infrastructure/database/db';
import { connectWithCapacityRetry } from './infrastructure/database/connection-capacity';

const port = Number(process.env.PORT || 4000);
const app = createApp();
const server = createServer(app);
const realtimeGateway = new DriverRealtimeGateway(server);
const broker = RabbitMQClient.getInstance();
let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
let reconnectAttempts = 0;
let realtimeStarting = false;
let stopping = false;
let databaseWarmupRunning = false;
let databaseWarmupTimer: ReturnType<typeof setTimeout> | undefined;

const warmDatabase = async (): Promise<void> => {
  if (stopping || databaseWarmupRunning) return;
  databaseWarmupRunning = true;
  try {
    const client = await connectWithCapacityRetry(() => pool.connect(), { timeoutMs: 30000 });
    client.release();
    if (!stopping) logger.info('[DB] Shared connection ready; retaining one connection for requests.');
  } catch (error) {
    if (!stopping) {
      logger.warn(`[DB] Waiting for database connection (${(error as { code?: string }).code || 'unavailable'}).`);
      databaseWarmupTimer = setTimeout(() => { databaseWarmupTimer = undefined; void warmDatabase(); }, 10000);
    }
  } finally { databaseWarmupRunning = false; }
};
pool.on('remove', () => {
  if (!stopping && !databaseWarmupRunning && !databaseWarmupTimer && pool.totalCount === 0) {
    databaseWarmupTimer = setTimeout(() => { databaseWarmupTimer = undefined; void warmDatabase(); }, 1000);
  }
});
void warmDatabase();

const scheduleRealtime = (delay: number) => {
  if (stopping || reconnectTimer) return;
  reconnectTimer = setTimeout(() => { reconnectTimer = undefined; void startRealtime(); }, delay);
};
broker.onDisconnected = () => scheduleRealtime(1000);

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

const shutdown = async () => {
  if (stopping) return;
  stopping = true;
  if (reconnectTimer) clearTimeout(reconnectTimer);
  if (databaseWarmupTimer) clearTimeout(databaseWarmupTimer);
  server.close();
  await Promise.allSettled([broker.close(), pool.end()]);
  process.exit(0);
};
process.once('SIGTERM', () => void shutdown());
process.once('SIGINT', () => void shutdown());

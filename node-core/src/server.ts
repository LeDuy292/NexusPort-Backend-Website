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
  if (stopping || realtimeStarting) return;
  realtimeStarting = true;
  try {
    await realtimeGateway.start();
    reconnectAttempts = 0;
    logger.info('[Realtime] RabbitMQ consumers ready.');
  } catch (error) {
    const delay = Math.min(5000 * 2 ** Math.min(reconnectAttempts++, 4), 60000);
    logger.warn(`[Realtime] RabbitMQ unavailable (${(error as { code?: string }).code || 'connection failed'}); retrying in ${delay / 1000}s. Ensure the configured broker is running.`);
    scheduleRealtime(delay);
  } finally {
    realtimeStarting = false;
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

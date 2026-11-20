import { Pool } from 'pg';
import { logger } from '../../shared/utils/logger';
import { connectWithCapacityRetry } from './connection-capacity';

function resolveConnectionString(): string {
  if (process.env.DATABASE_URL) {
    return process.env.DATABASE_URL;
  }

  const dbHost = process.env.DB_HOST || 'localhost';
  const dbPort = process.env.DB_PORT || '5432';
  const dbName = process.env.DB_NAME || 'nexusport';
  const dbUser = process.env.DB_USER || 'postgres';
  const dbPass = process.env.DB_PASSWORD || '';
  return `postgresql://${encodeURIComponent(dbUser)}:${encodeURIComponent(dbPass)}@${dbHost}:${dbPort}/${dbName}`;
}

const rawConnectionString = resolveConnectionString();
// Strip sslmode from query string to prevent pg-connection-string from forcing strict SSL verification
const connectionString = rawConnectionString
  .replace(/([?&])sslmode=[^&]+(&|$)/, '$1')
  .replace(/([?&])uselibpqcompat=[^&]+(&|$)/, '$1')
  .replace(/[?&]$/, '');

const isSslRequired =
  process.env.DB_SSL === 'true' ||
  process.env.DB_SSL_MODE === 'require' ||
  (rawConnectionString.includes('sslmode=') && !rawConnectionString.includes('sslmode=disable'));

const pool = new Pool({
  connectionString,
  application_name: 'nexusport-node-core',
  max: Number(process.env.DB_POOL_MAX || 2),
  min: 0,
  idleTimeoutMillis: 10000,
  connectionTimeoutMillis: 10000,
  ...(isSslRequired ? { ssl: { rejectUnauthorized: false } } : {}),
});

pool.on('connect', (client) => {
  client.on('error', (err) => {
    logger.warn('[DB] Handled PostgreSQL client error:', (err as Error)?.message || err);
  });
});

pool.on('error', (err: any) => {
  if (err?.code === '57P05' || err?.message?.includes('idle-session timeout')) {
    logger.debug('[DB] Idle PostgreSQL connection closed by server timeout; pool will reconnect on demand.');
    return;
  }
  logger.error('Unexpected error on idle PostgreSQL client', err);
});

export const query = async (text: string, params?: unknown[]) => {
  const start = Date.now();
  const client = await getClient();
  try {
    const res = await client.query(text, params);
    const duration = Date.now() - start;
    logger.debug('Executed query', { text, duration, rows: res.rowCount });
    return res;
  } finally { client.release(); }
};

export const getClient = () => connectWithCapacityRetry(() => pool.connect());

export default pool;

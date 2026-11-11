import { Pool } from 'pg';
import { logger } from '../../shared/utils/logger';

function resolveConnectionString(): string {
  if (process.env.DATABASE_URL) {
    return process.env.DATABASE_URL;
  }

  const aivenHost = process.env.AIVEN_DB_HOST;
  const aivenUser = process.env.AIVEN_DB_USER;
  const aivenPass = process.env.AIVEN_DB_PASSWORD;
  if (aivenHost && aivenUser && aivenPass) {
    const aivenPort = process.env.AIVEN_DB_PORT || '20134';
    const aivenDb = process.env.AIVEN_DB_NAME || 'defaultdb';
    return `postgresql://${encodeURIComponent(aivenUser)}:${encodeURIComponent(aivenPass)}@${aivenHost}:${aivenPort}/${aivenDb}?sslmode=${process.env.AIVEN_DB_SSLMODE || 'require'}`;
  }

  return 'postgresql://postgres:120104@localhost:5432/nexusport';
}

const connectionString = resolveConnectionString();
const isSslRequired =
  process.env.DB_SSL === 'true' ||
  process.env.DB_TARGET === 'aiven' ||
  (connectionString.includes('sslmode=') && !connectionString.includes('sslmode=disable'));

const pool = new Pool({
  connectionString,
  max: Number(process.env.DB_POOL_MAX || 2),
  idleTimeoutMillis: 10000,
  connectionTimeoutMillis: 10000,
  ...(isSslRequired ? { ssl: { rejectUnauthorized: false } } : {}),
});

pool.on('error', (err) => {
  logger.error('Unexpected error on idle PostgreSQL client', err);
});

export const query = async (text: string, params?: unknown[]) => {
  const start = Date.now();
  const res = await pool.query(text, params);
  const duration = Date.now() - start;
  logger.debug('Executed query', { text, duration, rows: res.rowCount });
  return res;
};

export const getClient = () => pool.connect();

export default pool;

import { Pool } from 'pg';
import { logger } from '../../shared/utils/logger';

const connectionString = process.env.DATABASE_URL || 'postgresql://postgres:11223344@localhost:5432/nexusport';
const isSslRequired =
  process.env.DB_SSL === 'true' ||
  process.env.DB_TARGET === 'aiven' ||
  connectionString.includes('sslmode=require');

const pool = new Pool({
  connectionString,
  max: Number(process.env.DB_POOL_MAX || 5),
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

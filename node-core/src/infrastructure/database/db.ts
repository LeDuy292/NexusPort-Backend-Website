import { Pool } from 'pg';
import { logger } from '../../shared/utils/logger';

const pool = new Pool({
  connectionString: process.env.DATABASE_URL || 'postgresql://postgres:pgadmin4@localhost:5432/nexusport',
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

export default pool;

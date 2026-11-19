'use strict';

/**
 * Validate và export các biến môi trường bắt buộc.
 * Throw lỗi ngay khi khởi động nếu thiếu biến quan trọng.
 */

// Không override nếu biến đã được set (quan trọng cho test environment)
require('dotenv').config({ override: false });

const hasDatabaseUrl = Boolean(process.env.DATABASE_URL);

function parseDatabaseUrl(rawUrl) {
  if (!rawUrl) return null;
  try {
    const parsed = new URL(rawUrl);
    return {
      host: parsed.hostname,
      port: parseInt(parsed.port, 10) || 5432,
      name: parsed.pathname.replace(/^\//, ''),
      user: decodeURIComponent(parsed.username),
      password: decodeURIComponent(parsed.password),
      sslMode: parsed.searchParams.get('sslmode') === 'disable' ? 'disable' : 'require',
    };
  } catch {
    return null;
  }
}

const localConfigured = Boolean(
  process.env.DB_HOST && process.env.DB_NAME && process.env.DB_USER
);
const parsedUrl = parseDatabaseUrl(process.env.DATABASE_URL);

const REQUIRED_VARS = [
  ...(!hasDatabaseUrl || !parsedUrl
    ? ['DB_HOST', 'DB_NAME', 'DB_USER']
    : []),
  'JWT_SECRET',
];

const missing = REQUIRED_VARS.filter((key) => !process.env[key]);
if (missing.length > 0) {
  console.error(`[Config] Thiếu biến môi trường bắt buộc: ${missing.join(', ')}`);
  console.error('[Config] Vui lòng copy .env.example thành .env và điền giá trị (hoặc cung cấp DATABASE_URL).');
  process.exit(1);
}

const useUrlFallback = Boolean(parsedUrl);

module.exports = {
  port: parseInt(process.env.PORT, 10) || 3001,
  nodeEnv: process.env.NODE_ENV || 'development',
  isProduction: process.env.NODE_ENV === 'production',
  isTest: process.env.NODE_ENV === 'test',

  db: useUrlFallback
    ? {
        logSql: process.env.DB_LOG_SQL === 'true',
        host: parsedUrl.host,
        port: parsedUrl.port,
        name: parsedUrl.name,
        user: parsedUrl.user,
        password: parsedUrl.password,
        sslMode: process.env.DB_SSL_MODE || parsedUrl.sslMode,
        poolMax: parseInt(process.env.DB_POOL_MAX, 10) || 10,
      }
    : {
        logSql: process.env.DB_LOG_SQL === 'true',
        host: process.env.DB_HOST,
        port: parseInt(process.env.DB_PORT, 10) || 5432,
        name: process.env.DB_NAME,
        user: process.env.DB_USER,
        password: process.env.DB_PASSWORD,
        sslMode: process.env.DB_SSL_MODE || 'disable',
        poolMax: parseInt(process.env.DB_POOL_MAX, 10) || 10,
      },

  jwt: {
    secret: process.env.JWT_SECRET,
    expiresIn: process.env.JWT_EXPIRES_IN || '8h',
  },

  bcrypt: {
    saltRounds: parseInt(process.env.BCRYPT_SALT_ROUNDS, 10) || 12,
  },
};

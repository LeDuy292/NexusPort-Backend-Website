'use strict';

/**
 * Validate và export các biến môi trường bắt buộc.
 * Throw lỗi ngay khi khởi động nếu thiếu biến quan trọng.
 */

// Không override nếu biến đã được set (quan trọng cho test environment)
require('dotenv').config({ override: false });

const useAivenDatabase = process.env.DB_TARGET === 'aiven';

const REQUIRED_VARS = [
  ...(useAivenDatabase
    ? ['AIVEN_DB_HOST', 'AIVEN_DB_PORT', 'AIVEN_DB_NAME', 'AIVEN_DB_USER', 'AIVEN_DB_PASSWORD']
    : ['DB_HOST', 'DB_NAME', 'DB_USER', 'DB_PASSWORD']),
  'JWT_SECRET',
];

const missing = REQUIRED_VARS.filter((key) => !process.env[key]);
if (missing.length > 0) {
  console.error(`[Config] Thiếu biến môi trường bắt buộc: ${missing.join(', ')}`);
  console.error('[Config] Vui lòng copy .env.example thành .env và điền giá trị.');
  process.exit(1);
}

module.exports = {
  port: parseInt(process.env.PORT, 10) || 3001,
  nodeEnv: process.env.NODE_ENV || 'development',
  isProduction: process.env.NODE_ENV === 'production',
  isTest: process.env.NODE_ENV === 'test',

  db: {
    host: useAivenDatabase ? process.env.AIVEN_DB_HOST : process.env.DB_HOST,
    port: parseInt(useAivenDatabase ? process.env.AIVEN_DB_PORT : process.env.DB_PORT, 10) || 5432,
    name: useAivenDatabase ? process.env.AIVEN_DB_NAME : process.env.DB_NAME,
    user: useAivenDatabase ? process.env.AIVEN_DB_USER : process.env.DB_USER,
    password: useAivenDatabase ? process.env.AIVEN_DB_PASSWORD : process.env.DB_PASSWORD,
    sslMode: useAivenDatabase ? (process.env.AIVEN_DB_SSLMODE || 'require') : (process.env.DB_SSL_MODE || 'disable'),
    poolMax: parseInt(process.env.DB_POOL_MAX, 10) || 5,
  },

  jwt: {
    secret: process.env.JWT_SECRET,
    expiresIn: process.env.JWT_EXPIRES_IN || '8h',
  },

  bcrypt: {
    saltRounds: parseInt(process.env.BCRYPT_SALT_ROUNDS, 10) || 12,
  },
};

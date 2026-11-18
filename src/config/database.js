'use strict';

const { Sequelize } = require('sequelize');
const { db } = require('./env');

const sequelize = new Sequelize(db.name, db.user, db.password, {
  host: db.host,
  port: db.port,
  dialect: 'postgres',
  dialectOptions: db.sslMode === 'require'
    ? { ssl: { require: true, rejectUnauthorized: false } }
    : {},
  // SQL tracing is explicit; connection failures and application errors still log.
  logging: db.logSql ? (msg) => console.log(`[DB] ${msg}`) : false,
  pool: {
    max: db.poolMax,
    min: 0,
    acquire: 30000,
    idle: 10000,
  },
  define: {
    underscored: true,       // snake_case columns
    timestamps: true,
    createdAt: 'created_at',
    updatedAt: 'updated_at',
  },
});

/**
 * Kiểm tra kết nối tới PostgreSQL.
 */
async function connectDB() {
  const maxRetries = 10;
  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    try {
      await sequelize.authenticate();
      console.log('[DB] Kết nối PostgreSQL thành công.');
      return;
    } catch (error) {
      console.warn(`[DB] Kết nối lần ${attempt} chưa thành công: ${error.message}`);
      if (attempt === maxRetries) {
        console.error('[DB] Không thể kết nối PostgreSQL:', error.message);
        throw error;
      }
      await new Promise(r => setTimeout(r, 2000));
    }
  }
}

/**
 * Đồng bộ schema (chỉ dùng trong dev/test, dùng migrations ở production).
 */
async function syncDB(options = {}) {
  await sequelize.sync(options);
  console.log('[DB] Schema đã đồng bộ.');
}

module.exports = { sequelize, connectDB, syncDB };

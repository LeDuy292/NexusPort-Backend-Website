'use strict';

const { Sequelize } = require('sequelize');
const { db, nodeEnv } = require('./env');

const sequelize = new Sequelize(db.name, db.user, db.password, {
  host: db.host,
  port: db.port,
  dialect: 'postgres',
  logging: nodeEnv === 'development' ? (msg) => console.log(`[DB] ${msg}`) : false,
  pool: {
    max: 10,
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
  try {
    await sequelize.authenticate();
    console.log('[DB] Kết nối PostgreSQL thành công.');
  } catch (error) {
    console.error('[DB] Không thể kết nối PostgreSQL:', error.message);
    throw error;
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

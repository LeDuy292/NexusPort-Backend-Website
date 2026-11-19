'use strict';

// Load env & validate trước khi làm gì khác
require('./src/config/env');

const http = require('http');
const app = require('./src/app');
const { connectDB, syncDB } = require('./src/config/database');
const { port, nodeEnv } = require('./src/config/env');
const { initSocket } = require('./src/config/socket');
const { startDispatchRealtime } = require('./src/modules/booking-dispatch/dispatch.realtime');

// Preload tất cả models để Sequelize nhận diện khi syncDB
require('./src/models/User');
require('./src/models/TransportTrip');
require('./src/models/YardGateOutRequest');

async function startServer() {
  try {
    // 1. Kết nối database
    await connectDB();

    // 2. Sync schema (chỉ dùng trong dev — dùng migrations ở production)
    if (nodeEnv !== 'production' && process.env.DB_SYNC === 'true') {
      try {
        // Migrations also own columns on tables shared with Sequelize models.
        await syncDB({ alter: { drop: false } });
      } catch (syncErr) {
        console.warn('[DB] Schema sync skipped/warning:', syncErr.message);
      }
    }

    // 3. Khởi động HTTP server & Socket.IO
    const server = http.createServer(app);
    initSocket(server);
    const stopDispatchRealtime = startDispatchRealtime();
    server.on('close', stopDispatchRealtime);

    server.listen(port, () => {
      console.log('');
      console.log('╔══════════════════════════════════════════════════════════════════╗');
      console.log('║                  NexusPort API Server                            ║');
      console.log('╠══════════════════════════════════════════════════════════════════╣');
      console.log(`║  Environment : ${nodeEnv.padEnd(50)}║`);
      console.log(`║  Port        : ${String(port).padEnd(50)}║`);
      console.log(`║  API Base    : ${`http://localhost:${port}/api`.padEnd(50)}║`);
      console.log(`║  Swagger JS  : ${`http://localhost:${port}/api-docs`.padEnd(50)}║`);
      console.log(`║  Realtime    : ${`ws://localhost:${port} (Socket.IO enabled)`.padEnd(50)}║`);
      console.log('╚══════════════════════════════════════════════════════════════════╝');
      console.log('');
    });

    // Graceful shutdown để giải phóng port 3001 và connection pool PostgreSQL khi nodemon reload hoặc server tắt
    let isShuttingDown = false;
    const gracefulShutdown = (signal) => {
      if (isShuttingDown) return;
      isShuttingDown = true;
      server.close(() => {
        try {
          const { sequelize } = require('./src/config/database');
          sequelize.close().catch(() => {}).finally(() => {
            if (signal === 'SIGUSR2') {
              process.kill(process.pid, 'SIGUSR2');
            } else {
              process.exit(0);
            }
          });
        } catch {
          process.exit(0);
        }
      });
      // Dự phòng force exit sau 3 giây nếu server.close bị treo
      setTimeout(() => process.exit(0), 3000).unref();
    };

    process.once('SIGUSR2', () => gracefulShutdown('SIGUSR2'));
    process.on('SIGINT', () => gracefulShutdown('SIGINT'));
    process.on('SIGTERM', () => gracefulShutdown('SIGTERM'));
  } catch (error) {
    console.error('[Server] Khởi động thất bại:', error.message);
    process.exit(1);
  }
}

startServer();

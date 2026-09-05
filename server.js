'use strict';

// Load env & validate trước khi làm gì khác
require('./src/config/env');

const app = require('./src/app');
const { connectDB, syncDB } = require('./src/config/database');
const { port, nodeEnv } = require('./src/config/env');

async function startServer() {
  try {
    // 1. Kết nối database
    await connectDB();

    // 2. Sync schema (chỉ dùng trong dev — dùng migrations ở production)
    if (nodeEnv !== 'production') {
      await syncDB({ alter: true });
    }

    // 3. Khởi động HTTP server
    app.listen(port, () => {
      console.log('');
      console.log('╔══════════════════════════════════════════════════════════════════╗');
      console.log('║                  NexusPort API Server                            ║');
      console.log('╠══════════════════════════════════════════════════════════════════╣');
      console.log(`║  Environment : ${nodeEnv.padEnd(50)}║`);
      console.log(`║  Port        : ${String(port).padEnd(50)}║`);
      console.log(`║  API Base    : ${`http://localhost:${port}/api`.padEnd(50)}║`);
      console.log(`║  Swagger JS  : ${`http://localhost:${port}/api-docs`.padEnd(50)}║`);
      console.log('║  Swagger TS  : http://localhost:4000/api-docs                    ║');
      console.log('║  Swagger C#  : http://localhost:5000                             ║');
      console.log('╚══════════════════════════════════════════════════════════════════╝');
      console.log('');
    });
  } catch (error) {
    console.error('[Server] Khởi động thất bại:', error.message);
    process.exit(1);
  }
}

startServer();

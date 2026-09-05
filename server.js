'use strict';

// Load env & validate trước khi làm gì khác
require('./src/config/env');

const app = require('./src/app');
const { connectDB, syncDB } = require('./src/config/database');
const { port, nodeEnv } = require('./src/config/env');
const { printStartupBanner } = require('./src/utils/startupBanner');

async function startServer() {
  try {
    // 1. Kết nối database
    await connectDB();

    // 2. Sync schema (chỉ dùng trong dev — dùng migrations ở production)
    if (nodeEnv !== 'production') {
      await syncDB({ alter: true });
    }

    // 3. Khởi động HTTP server
    app.listen(port, async () => {
      await printStartupBanner({ port, nodeEnv });
    });
  } catch (error) {
    console.error('[Server] Khởi động thất bại:', error.message);
    process.exit(1);
  }
}

startServer();

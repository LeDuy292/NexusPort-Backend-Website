import dotenv from 'dotenv';
dotenv.config();

import { createApp } from './api/app';
import { logger } from './shared/utils/logger';

const PORT = process.env.PORT || 4000;
const app = createApp();

app.listen(PORT, () => {
  logger.info(`🚀 NexusPort Node.js Core Service listening on port ${PORT}`);
  logger.info(`👉 Health check: http://localhost:${PORT}/api/v1/health`);
});

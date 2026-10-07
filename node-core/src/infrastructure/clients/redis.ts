import { logger } from '../../shared/utils/logger';

export class RedisClient {
  private static instance: RedisClient;
  private isConnected = false;

  private constructor() {}

  public static getInstance(): RedisClient {
    if (!RedisClient.instance) {
      RedisClient.instance = new RedisClient();
    }
    return RedisClient.instance;
  }

  public async connect(): Promise<void> {
    logger.info('[RedisClient] Connecting to Redis...');
    this.isConnected = true;
  }

  public async get(_key: string): Promise<string | null> {
    return null;
  }

  public async set(key: string, _value: string, _ttlSeconds?: number): Promise<void> {
    logger.debug(`[RedisClient] SET ${key}`);
  }
}

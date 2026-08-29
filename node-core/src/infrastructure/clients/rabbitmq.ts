import { logger } from '../../shared/utils/logger';

export class RabbitMQClient {
  private static instance: RabbitMQClient;

  private constructor() {}

  public static getInstance(): RabbitMQClient {
    if (!RabbitMQClient.instance) {
      RabbitMQClient.instance = new RabbitMQClient();
    }
    return RabbitMQClient.instance;
  }

  public async connect(): Promise<void> {
    logger.info('[RabbitMQClient] Connecting to RabbitMQ broker...');
  }

  public async publish(queue: string, message: unknown): Promise<void> {
    logger.debug(`[RabbitMQClient] Publish to ${queue}:`, message);
  }
}

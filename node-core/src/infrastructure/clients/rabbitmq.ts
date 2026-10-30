import amqp, { Channel, ChannelModel, ConsumeMessage } from 'amqplib';
import { logger } from '../../shared/utils/logger';

export class RabbitMQClient {
  private static instance: RabbitMQClient;
  private connection: ChannelModel | null = null;
  private channel: Channel | null = null;

  private constructor() {}

  public static getInstance(): RabbitMQClient {
    if (!RabbitMQClient.instance) RabbitMQClient.instance = new RabbitMQClient();
    return RabbitMQClient.instance;
  }

  public async connect(): Promise<void> {
    if (this.channel) return;
    const url = process.env.RABBITMQ_URL || 'amqp://guest:guest@localhost:5672';
    const connection = await amqp.connect(url);
    const channel = await connection.createChannel();
    this.connection = connection;
    this.channel = channel;
    connection.on('error', (error: Error) => logger.error('[RabbitMQClient] Connection error:', error));
    connection.on('close', () => {
      logger.warn('[RabbitMQClient] Connection closed.');
      this.connection = null;
      this.channel = null;
    });
    logger.info('[RabbitMQClient] Connected to RabbitMQ broker.');
  }

  public async publish(queue: string, message: unknown): Promise<void> {
    await this.connect();
    if (!this.channel) throw new Error('RabbitMQ channel is unavailable.');
    await this.channel.assertQueue(queue, { durable: true });
    const sent = this.channel.sendToQueue(queue, Buffer.from(JSON.stringify(message)), {
      contentType: 'application/json',
      persistent: true,
      type: queue,
    });
    if (!sent) throw new Error(`RabbitMQ backpressure prevented publishing to ${queue}.`);
  }

  public async consume(queue: string, handler: (message: ConsumeMessage) => Promise<void>): Promise<void> {
    await this.connect();
    if (!this.channel) throw new Error('RabbitMQ channel is unavailable.');
    await this.channel.assertQueue(queue, { durable: true });
    await this.channel.prefetch(10);
    await this.channel.consume(queue, async message => {
      if (!message || !this.channel) return;
      try {
        await handler(message);
        this.channel.ack(message);
      } catch (error) {
        logger.error(`[RabbitMQClient] Failed to process ${queue}:`, error);
        this.channel.nack(message, false, false);
      }
    }, { noAck: false });
    logger.info(`[RabbitMQClient] Consuming ${queue}.`);
  }
}

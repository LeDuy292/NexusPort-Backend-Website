import amqp, { Channel, ChannelModel, ConsumeMessage } from 'amqplib';
import { logger } from '../../shared/utils/logger';

export class RabbitMQClient {
  private static instance: RabbitMQClient;
  private connection: ChannelModel | null = null;
  private channel: Channel | null = null;
  private connecting: Promise<void> | null = null;
  private consumers = new Set<string>();
  public onDisconnected?: () => void;

  private constructor() {}

  public static getInstance(): RabbitMQClient {
    if (!RabbitMQClient.instance) RabbitMQClient.instance = new RabbitMQClient();
    return RabbitMQClient.instance;
  }

  public async connect(): Promise<void> {
    if (this.channel) return;
    if (this.connecting) return this.connecting;
    this.connecting = this.open();
    try { await this.connecting; } finally { this.connecting = null; }
  }

  private async open(): Promise<void> {
    const url = process.env.RABBITMQ_URL || 'amqp://guest:guest@localhost:5672';
    const connection = await amqp.connect(url, { timeout: 5000, noDelay: true });
    this.connection = connection;
    connection.on('error', (error: Error) => logger.warn(`[RabbitMQClient] Connection interrupted: ${error.message}`));
    connection.on('close', () => {
      if (this.connection !== connection) return;
      this.connection = null;
      this.channel = null;
      this.consumers.clear();
      this.onDisconnected?.();
    });
    try {
      const channel = await connection.createChannel();
      this.channel = channel;
      channel.on('error', (error: Error) => logger.warn(`[RabbitMQClient] Channel interrupted: ${error.message}`));
      channel.on('close', () => {
        if (this.channel !== channel) return;
        this.channel = null;
        this.consumers.clear();
        void connection.close().catch(() => {});
      });
    } catch (error) {
      this.connection = null;
      await connection.close().catch(() => {});
      throw error;
    }
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
    if (this.consumers.has(queue)) return;
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
    this.consumers.add(queue);
    logger.info(`[RabbitMQClient] Consuming ${queue}.`);
  }

  public async close(): Promise<void> {
    const connection = this.connection;
    this.connection = null;
    this.channel = null;
    this.consumers.clear();
    if (connection) await connection.close();
  }
}

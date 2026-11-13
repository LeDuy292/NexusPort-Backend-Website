import { EventEmitter } from 'events';
import amqp from 'amqplib';
import { RabbitMQClient } from './rabbitmq';

jest.mock('amqplib', () => ({ __esModule: true, default: { connect: jest.fn() } }));
describe('RabbitMQ connection lifecycle', () => {
  const broker = RabbitMQClient.getInstance();
  const makeConnection = () => {
    const channel = Object.assign(new EventEmitter(), {
      assertQueue: jest.fn().mockResolvedValue({}), prefetch: jest.fn().mockResolvedValue(undefined),
      consume: jest.fn().mockResolvedValue({ consumerTag: 'test' }),
    });
    const connection = Object.assign(new EventEmitter(), {
      createChannel: jest.fn().mockResolvedValue(channel),
      close: jest.fn().mockImplementation(async () => { connection.emit('close'); }),
    });
    return { connection, channel };
  };
  beforeEach(async () => { broker.onDisconnected = undefined; await broker.close(); jest.clearAllMocks(); });
  afterEach(async () => { broker.onDisconnected = undefined; await broker.close(); });

  it('shares one pending connection across concurrent callers', async () => {
    const { connection } = makeConnection();
    jest.mocked(amqp.connect).mockResolvedValue(connection as never);
    await Promise.all([broker.connect(), broker.connect(), broker.connect()]);
    expect(amqp.connect).toHaveBeenCalledTimes(1);
    expect(connection.createChannel).toHaveBeenCalledTimes(1);
  });
  it('does not register the same queue twice after partial startup retry', async () => {
    const { connection, channel } = makeConnection();
    jest.mocked(amqp.connect).mockResolvedValue(connection as never);
    await broker.consume('test.queue', async () => {});
    await broker.consume('test.queue', async () => {});
    expect(channel.consume).toHaveBeenCalledTimes(1);
  });
  it('clears consumers and notifies reconnect after broker disconnects', async () => {
    const first = makeConnection(), second = makeConnection();
    jest.mocked(amqp.connect).mockResolvedValueOnce(first.connection as never).mockResolvedValueOnce(second.connection as never);
    const reconnect = jest.fn(); broker.onDisconnected = reconnect;
    await broker.consume('test.queue', async () => {});
    first.connection.emit('close');
    expect(reconnect).toHaveBeenCalledTimes(1);
    await broker.consume('test.queue', async () => {});
    expect(second.channel.consume).toHaveBeenCalledTimes(1);
  });
  it('can retry after a refused connection and suppresses reconnect on explicit shutdown', async () => {
    const { connection } = makeConnection();
    jest.mocked(amqp.connect).mockRejectedValueOnce(new Error('ECONNREFUSED')).mockResolvedValueOnce(connection as never);
    await expect(broker.connect()).rejects.toThrow('ECONNREFUSED');
    await broker.connect();
    const reconnect = jest.fn(); broker.onDisconnected = reconnect;
    await broker.close();
    expect(reconnect).not.toHaveBeenCalled();
  });
});

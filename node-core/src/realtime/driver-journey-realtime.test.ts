import { createServer } from 'http';
import { AddressInfo } from 'net';
import jwt from 'jsonwebtoken';
import { io, Socket } from 'socket.io-client';
import { DriverRealtimeGateway } from './driver-realtime.gateway';
import { DriverRouteRepository } from '../modules/driver/infrastructure/driver-route.repository';

describe('driver journey realtime identity and scope', () => {
  const account = '11111111-1111-4111-8111-111111111111';
  const driver = '22222222-2222-4222-8222-222222222222';
  const secret = process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!';
  let server: ReturnType<typeof createServer>;
  let gateway: DriverRealtimeGateway;
  let socket: Socket;
  beforeEach(async () => {
    jest.spyOn(DriverRouteRepository.prototype, 'resolveDriverId').mockResolvedValue(driver);
    server = createServer();
    gateway = new DriverRealtimeGateway(server);
    await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
  });
  afterEach(async () => {
    socket?.disconnect();
    await new Promise<void>(resolve => (gateway as any).io.close(resolve));
    jest.restoreAllMocks();
  });

  it('joins the linked business driver room and receives only the journey invalidation payload', async () => {
    socket = io(`http://127.0.0.1:${(server.address() as AddressInfo).port}`, {
      auth: { token: jwt.sign({ sub: account, role: 'Driver' }, secret) }, transports: ['websocket'], reconnection: false,
    });
    await new Promise<void>((resolve, reject) => { socket.on('connect', resolve); socket.on('connect_error', reject); });
    expect(DriverRouteRepository.prototype.resolveDriverId).toHaveBeenCalledWith(account);
    const rooms = (gateway as any).io.sockets.adapter.rooms;
    expect(rooms.has(`driver:${driver}`)).toBe(true);
    expect(rooms.has(`driver:${account}`)).toBe(false);
    const event = { eventId: account, domain: 'gate', entityId: 'gate-record', status: 'completed',
      driverId: driver, bookingId: driver, occurredAt: '2026-10-18T01:00:00.000Z', metadata: { internal: 'omit' } };
    const received = new Promise<Record<string, unknown>>(resolve => socket.once('driver.journey.updated', resolve));
    (gateway as any).emitToDispatcher(event);
    expect(await received).toEqual({ eventId: account, domain: 'gate', bookingId: driver, occurredAt: event.occurredAt });
  });

  it('refuses driver accounts that have no active linked profile', async () => {
    jest.mocked(DriverRouteRepository.prototype.resolveDriverId).mockResolvedValue(null);
    socket = io(`http://127.0.0.1:${(server.address() as AddressInfo).port}`, {
      auth: { token: jwt.sign({ sub: account, role: 'Driver' }, secret) }, transports: ['websocket'], reconnection: false,
    });
    const error = await new Promise<Error>(resolve => socket.once('connect_error', resolve));
    expect(error.message).toMatch(/linked driver/);
  });
});

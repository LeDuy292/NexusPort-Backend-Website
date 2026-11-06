import express, { NextFunction, Request, Response } from 'express';
import jwt from 'jsonwebtoken';
import { createDriverRouter } from './driver.controller';
import { DriverRouteService, toDriverRoute } from '../application/driver-route.service';
import { AppError } from '../../../shared/errors/app-error';
import { createServer, get as httpGet, IncomingHttpHeaders } from 'http';
import { AddressInfo } from 'net';
import { DriverRouteRepository } from '../infrastructure/driver-route.repository';
import { ConfiguredYardMapProvider } from '../infrastructure/yard-map.provider';

const driverId = '55555555-5555-4555-8555-555555555555';
const containerId = '11111111-1111-4111-8111-111111111111';
const secret = process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!';
const token = (role = 'Driver') => jwt.sign({ sub: driverId, role }, secret, { algorithm: 'HS256' });
const app = express();
app.use('/drivers', createDriverRouter());
app.use((error: AppError, _req: Request, res: Response, _next: NextFunction) => {
  res.status(error.statusCode || 500).json({ code: error.errorCode });
});
const server = createServer(app);
const get = (path: string, bearer?: string): Promise<{ status: number; headers: IncomingHttpHeaders }> => new Promise((resolve, reject) => {
  const address = server.address() as AddressInfo;
  httpGet({ hostname: '127.0.0.1', port: address.port, path,
    headers: bearer ? { authorization: `Bearer ${bearer}` } : {},
  }, response => {
    response.resume();
    response.on('end', () => resolve({ status: response.statusCode!, headers: response.headers }));
  }).on('error', reject);
});

describe('Driver Smart Route API authentication and input', () => {
  beforeAll(async () => { await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve)); });
  afterAll(async () => { await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve())); });
  afterEach(() => jest.restoreAllMocks());
  beforeEach(() => { jest.spyOn(DriverRouteRepository.prototype, 'resolveDriverId').mockResolvedValue(driverId); });

  it('requires a valid Driver token', async () => {
    expect((await get('/drivers/me/routes')).status).toBe(401);
    expect((await get('/drivers/me/routes', 'bad-token')).status).toBe(401);
    expect((await get('/drivers/me/routes', token('Dispatcher'))).status).toBe(403);
  });

  it('protects the pre-entry map with linked Driver authentication and disables caching', async () => {
    const load = jest.spyOn(ConfiguredYardMapProvider.prototype, 'load').mockResolvedValue({
      map: { version: 'approved-1', nodes: [{ id: 'gate', label: 'Gate', x: 0, y: 0, restricted: false }], edges: [] },
    });
    expect((await get('/drivers/me/yard-map')).status).toBe(401);
    expect((await get('/drivers/me/yard-map', token('Dispatcher'))).status).toBe(403);
    expect(load).not.toHaveBeenCalled();
    const response = await get('/drivers/me/yard-map', token());
    expect(response.status).toBe(200);
    expect(response.headers['cache-control']).toBe('no-store');
    expect(load).toHaveBeenCalledWith(null);
  });

  it('validates container ID and rejects structured current position query parameters', async () => {
    const calculate = jest.spyOn(DriverRouteService.prototype, 'get');
    expect((await get('/drivers/me/routes/not-a-uuid', token())).status).toBe(422);
    expect((await get(`/drivers/me/routes/${containerId}?currentNodeId[]=gate`, token())).status).toBe(422);
    expect(calculate).not.toHaveBeenCalled();
  });

  it('scopes route calculation to the token identity and disables caching', async () => {
    const result = toDriverRoute({ containerId, containerNumber: 'CSQU3054383', bookingId: driverId,
      gateTransactionId: driverId, gateCode: 'G1', gateStatus: 'allowed', slotId: null,
      zone: null, blockCode: null, bay: null, row: null, tier: null, instructionId: null,
      fromGate: null, routeDetail: null, mapData: null });
    const calculate = jest.spyOn(DriverRouteService.prototype, 'get').mockResolvedValue(result);
    const response = await get(`/drivers/me/routes/${containerId}?currentNodeId=road-2`, token());
    expect(response.status).toBe(200);
    expect(response.headers['cache-control']).toBe('no-store');
    expect(calculate).toHaveBeenCalledWith(driverId, containerId, 'road-2');
  });
});

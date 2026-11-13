import { connectWithCapacityRetry } from './connection-capacity';
import { errorHandler } from '../../api/middleware/error.middleware';
import { Request, Response, NextFunction } from 'express';

const full = () => Object.assign(new Error('remaining connection slots reserved'), { code: '53300' });
describe('PostgreSQL connection capacity handling', () => {
  it('waits for a free connection slot and returns the acquired client', async () => {
    let clock = 0;
    const client = { release: jest.fn() };
    const connect = jest.fn().mockRejectedValueOnce(full()).mockRejectedValueOnce(full()).mockResolvedValue(client);
    const delay = jest.fn(async (ms: number) => { clock += ms; });
    expect(await connectWithCapacityRetry(connect, { now: () => clock, delay })).toBe(client);
    expect(connect).toHaveBeenCalledTimes(3);
    expect(client.release).not.toHaveBeenCalled();
  });
  it('stops when the capacity deadline expires instead of retrying forever', async () => {
    let clock = 0;
    const connect = jest.fn().mockRejectedValue(full());
    const delay = jest.fn(async (ms: number) => { clock += ms; });
    await expect(connectWithCapacityRetry(connect, { timeoutMs: 1250, now: () => clock, delay })).rejects.toMatchObject({ code: '53300' });
    expect(clock).toBe(1250);
    expect(connect).toHaveBeenCalledTimes(3);
    expect(delay.mock.calls.map(([ms]) => ms)).toEqual([500, 750]);
  });
  it('does not retry authentication errors or arbitrary failures', async () => {
    const error = Object.assign(new Error('password authentication failed'), { code: '28P01' });
    const connect = jest.fn().mockRejectedValue(error), delay = jest.fn();
    await expect(connectWithCapacityRetry(connect, { delay })).rejects.toBe(error);
    expect(connect).toHaveBeenCalledTimes(1);
    expect(delay).not.toHaveBeenCalled();
  });
  it('returns 503 with Retry-After for API capacity failures', () => {
    const res = { setHeader: jest.fn(), status: jest.fn().mockReturnThis(), json: jest.fn() };
    errorHandler(full(), { method: 'GET', path: '/shipping-line/manifests' } as Request, res as unknown as Response, jest.fn() as NextFunction);
    expect(res.setHeader).toHaveBeenCalledWith('Retry-After', '3');
    expect(res.status).toHaveBeenCalledWith(503);
    expect(res.json).toHaveBeenCalledWith(expect.objectContaining({ success: false, error: expect.objectContaining({ code: 'DB_CONNECTION_LIMIT' }) }));
  });
});

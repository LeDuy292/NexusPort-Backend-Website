'use strict';
jest.mock('../src/config/database', () => ({
  sequelize: { query: jest.fn(), transaction: jest.fn() },
}));
jest.mock('../src/config/socket', () => ({
  getIO: jest.fn(), emitToDispatchers: jest.fn(), emitToDriver: jest.fn(),
}));
const { sequelize } = require('../src/config/database');
const { getIO, emitToDispatchers, emitToDriver } = require('../src/config/socket');
const { pumpDispatchEvents, startDispatchRealtime } = require('../src/modules/booking-dispatch/dispatch.realtime');
const transaction = { id: 'worker-transaction' };

beforeEach(() => {
  jest.clearAllMocks();
  getIO.mockReturnValue({});
  sequelize.query.mockReset();
  sequelize.transaction.mockImplementation((options, callback) => callback(transaction));
});
afterEach(() => { jest.useRealTimers(); jest.restoreAllMocks(); });

test('idle worker does not open transactions or log polling SQL', async () => {
  sequelize.query.mockResolvedValueOnce([[]]).mockResolvedValueOnce([]);
  await pumpDispatchEvents();
  expect(sequelize.query).toHaveBeenCalledTimes(2);
  expect(sequelize.transaction).not.toHaveBeenCalled();
  for (const [, options] of sequelize.query.mock.calls) expect(options.logging).toBe(false);
  expect(emitToDispatchers).not.toHaveBeenCalled();
});

test('pending events are locked, sent to the assigned login and marked published without SQL spam', async () => {
  const event = { id: 'event-1', booking_id: 'booking-1', version: 2, kind: 'called', created_at: new Date(), user_id: 'login-1' };
  sequelize.query.mockResolvedValueOnce([[]]).mockResolvedValueOnce([{id:event.id}])
    .mockResolvedValueOnce([event]).mockResolvedValueOnce([]);
  await pumpDispatchEvents();
  expect(sequelize.transaction).toHaveBeenCalledWith({logging:false}, expect.any(Function));
  expect(sequelize.query.mock.calls[2][0]).toContain('FOR UPDATE OF e SKIP LOCKED');
  expect(emitToDispatchers).toHaveBeenCalledWith('booking:dispatch_updated', expect.objectContaining({eventId:event.id,version:2}));
  expect(emitToDriver).toHaveBeenCalledWith('login-1','booking:dispatch_updated',expect.objectContaining({bookingId:event.booking_id}));
  expect(sequelize.query.mock.calls[3][0]).toContain('SET published_at=now()');
  expect(sequelize.query.mock.calls[3][1]).toMatchObject({transaction,replacements:{id:event.id}});
  for (const [, options] of sequelize.query.mock.calls) expect(options.logging).toBe(false);
});

test('expired calls still lock the booking and return it to ready without SQL spam', async () => {
  sequelize.query.mockResolvedValueOnce([[{booking_id:'booking-1'}]])
    .mockResolvedValueOnce([]).mockResolvedValueOnce([]).mockResolvedValueOnce([]);
  await pumpDispatchEvents();
  expect(sequelize.transaction).toHaveBeenCalledWith({logging:false},expect.any(Function));
  expect(sequelize.query.mock.calls[1][0]).toContain('FOR UPDATE OF b,d');
  expect(sequelize.query.mock.calls[2][0]).toContain("THEN 'entry_expired' ELSE 'call_expired'");
  expect(sequelize.query.mock.calls[2][0]).toContain("status='ready'");
  expect(sequelize.query.mock.calls[2][1]).toMatchObject({transaction,replacements:{id:'booking-1'}});
  for (const [, options] of sequelize.query.mock.calls) expect(options.logging).toBe(false);
});

test('background recovery polls every five seconds and stops on cleanup', async () => {
  jest.useFakeTimers();
  sequelize.query.mockImplementation(async sql => sql.includes('SELECT booking_id') ? [[]] : []);
  const stop = startDispatchRealtime();
  try {
    await pumpDispatchEvents();
    expect(sequelize.query).toHaveBeenCalledTimes(2);
    await jest.advanceTimersByTimeAsync(4999);
    expect(sequelize.query).toHaveBeenCalledTimes(2);
    await jest.advanceTimersByTimeAsync(1);
    expect(sequelize.query).toHaveBeenCalledTimes(4);
    stop();
    await jest.advanceTimersByTimeAsync(10000);
    expect(sequelize.query).toHaveBeenCalledTimes(4);
  } finally { stop(); }
});

test('overlapping pumps share a single run', async () => {
  let finish;
  sequelize.query.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; })).mockResolvedValueOnce([]);
  const first = pumpDispatchEvents();
  const second = pumpDispatchEvents();
  expect(second).toBe(first);
  finish([[]]);
  await first;
  expect(sequelize.query).toHaveBeenCalledTimes(2);
});

test('worker errors remain visible and a later pump can retry', async () => {
  const log = jest.spyOn(console,'error').mockImplementation(() => {});
  sequelize.query.mockRejectedValueOnce(new Error('database unavailable'));
  await pumpDispatchEvents();
  expect(log).toHaveBeenCalledWith('[Booking dispatch realtime]','database unavailable');
  sequelize.query.mockResolvedValueOnce([[]]).mockResolvedValueOnce([]);
  await pumpDispatchEvents();
  expect(sequelize.query).toHaveBeenCalledTimes(3);
});

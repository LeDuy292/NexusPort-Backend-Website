'use strict';
jest.mock('../src/config/database', () => ({
  sequelize: { query: jest.fn(), transaction: jest.fn(fn => fn({ id: 'test-transaction' })) },
}));
jest.mock('../src/models/User', () => ({ User: { findByPk: jest.fn() } }));
jest.mock('../src/modules/booking-dispatch/dispatch.realtime', () => ({ pumpDispatchEvents: jest.fn() }));
jest.mock('../src/modules/booking-dispatch/yard-admission', () => ({
  ...jest.requireActual('../src/modules/booking-dispatch/yard-admission'),
  lock: jest.fn(), requireSpace: jest.fn(), assignDestination: jest.fn(async () => true), snapshot: jest.fn(),
}));
const express = require('express');
const request = require('supertest');
const { sequelize } = require('../src/config/database');
const { User } = require('../src/models/User');
const { generateToken } = require('../src/utils/jwt');
const service = require('../src/modules/booking-dispatch/dispatch.service');
const { getNewManifestAppointment } = require('../src/modules/booking-dispatch/manifest-appointment');
const admission = require('../src/modules/booking-dispatch/yard-admission');
const { decorate, checkCall, checkArrival } = require('../src/modules/booking-dispatch/dispatch.policy');
const router = require('../src/modules/booking-dispatch/dispatch.routes');
const app = express(); app.use(express.json()); app.use('/dispatch', router);
const id = '11111111-1111-1111-1111-111111111111';
const now = new Date('2026-10-17T03:00:00Z');
const base = { id, driverId: 'driver-profile', status: 'approved', appointmentStart: '2026-10-17T03:00:00Z', appointmentEnd: '2026-10-17T04:00:00Z', dispatch: null };
const token = role => generateToken({ id, role });
beforeEach(() => { sequelize.query.mockReset(); User.findByPk.mockResolvedValue({is_active:true}); });
test('arrival opens exactly 12 hours before appointment, including across dates', () => {
  const beforeWindow = new Date('2026-10-16T14:59:59Z');
  const opensAt = new Date('2026-10-16T15:00:00Z');
  expect(() => checkArrival(base, beforeWindow)).toThrow('12 giờ');
  expect(decorate(base, beforeWindow).coordination.canArrive).toBe(false);
  expect(() => checkArrival(base, opensAt)).not.toThrow();
  expect(decorate(base, opensAt).coordination.canArrive).toBe(true);
  expect(() => checkArrival(base, new Date('2026-10-16T20:00:00Z'))).not.toThrow();
});

test('pending/canceled bookings cannot confirm arrival within the window', () => {
  expect(() => checkArrival({...base,status:'pending'},now)).toThrow();
  expect(() => checkArrival({...base,status:'canceled'},now)).toThrow();
  expect(decorate({...base,status:'pending'},now).coordination.canArrive).toBe(false);
  expect(decorate({...base,status:'canceled'},now).coordination.canArrive).toBe(false);
});
test('ready and unheld vehicles can be called from exactly 12 hours before appointment', () => {
  expect(() => checkCall(base,now)).toThrow();
  const ready = {...base,dispatch:{status:'ready'}};
  expect(() => checkCall(ready,now)).not.toThrow();
  const beforeWindow = new Date('2026-10-16T14:59:59Z');
  const opensAt = new Date('2026-10-16T15:00:00Z');
  expect(() => checkCall(ready,beforeWindow)).toThrow('12 giờ');
  expect(decorate(ready,beforeWindow).coordination.canCall).toBe(false);
  expect(() => checkCall(ready,opensAt)).not.toThrow();
  expect(decorate(ready,opensAt).coordination.canCall).toBe(true);
  expect(() => checkCall(ready,new Date('2026-10-17T02:59:59Z'))).not.toThrow();
  expect(() => checkCall({...ready,dispatch:{status:'ready',holdUntil:'2026-10-17T03:10:00Z'}},now)).toThrow();
});

test('manifest only creates an appointment from an explicit window or the registered trip for that exact container', async () => {
  const order = { eportOrderId: id, containerId: 'CMAU3381923' };
  const trip = { sourceContainerId: 'line-1', appointmentStart: '2026-11-19T08:00:00.0000000+07:00', appointmentEnd: '2026-11-19T10:00:00+07:00' };
  const snapshot = { content: {
    lines: [{ sourceContainerId: 'line-1', declaration: { containerNumber: order.containerId } }],
    delivery: { trips: [trip] },
  } };
  const read = jest.fn().mockResolvedValue([{ snapshot }]);
  await expect(getNewManifestAppointment(order, read)).resolves.toEqual({
    start: new Date('2026-11-19T01:00:00Z'), end: new Date('2026-11-19T03:00:00Z'),
  });
  await expect(getNewManifestAppointment({ ...order, containerId: 'SAME-LAST-3381923' }, read)).resolves.toBeNull();
  await expect(getNewManifestAppointment({ paidAt: trip.appointmentStart, eta: '08:30' }, read)).resolves.toBeNull();
  await expect(getNewManifestAppointment({ appointmentStart: '2026-11-19T08:00', appointmentEnd: '2026-11-19T10:00' }, read))
    .resolves.toEqual({ start: new Date('2026-11-19T01:00:00Z'), end: new Date('2026-11-19T03:00:00Z') });
});

test('manifest retries never update an existing appointment or write an arrival event', async () => {
  const originalEnv = process.env.NODE_ENV;
  process.env.NODE_ENV = 'development';
  const manifest = { id: 'BK-PAID', status: 'Ready', driverName: 'Driver One', licensePlate: '43C-123.45' };
  const existing = {
    id, carrier_id: 'carrier-1', driver_id: 'driver-1', truck_id: 'truck-1',
    status: 'approved', booking_type: 'dropoff', description: null, appointment_start: base.appointmentStart,
  };
  sequelize.transaction
    .mockImplementationOnce((_options, fn) => fn({ id: 'first-sync' }))
    .mockImplementationOnce((_options, fn) => fn({ id: 'second-sync' }));
  sequelize.query.mockImplementation(async sql => {
    if (sql.includes('FROM shipping_manifests')) return [{ bookings: [manifest] }];
    if (sql.includes('FROM drivers')) return [{ id: 'driver-1', full_name: manifest.driverName, carrier_id: 'carrier-1' }];
    if (sql.includes('FROM trucks')) return [{ id: 'truck-1', plate_number: manifest.licensePlate }];
    if (sql.includes('FROM bookings WHERE')) return [existing];
    if (sql.includes('UPDATE bookings')) { existing.status = 'ready'; return []; }
    return [];
  });
  try {
    await service.syncManifestGateBookings();
    await service.syncManifestGateBookings();
  } finally {
    process.env.NODE_ENV = originalEnv;
  }
  const mutations = sequelize.query.mock.calls.map(([sql]) => sql).filter(sql => /\b(INSERT INTO|UPDATE)\b/.test(sql.replace('FOR UPDATE','')));
  expect(mutations).toHaveLength(1);
  expect(mutations[0]).toContain('UPDATE bookings');
  expect(mutations[0]).not.toMatch(/appointment_start|appointment_end|valid_from|valid_to/);
});

test.each(['arrived','called','acknowledged','withdrawn'])('%s vehicles cannot be called again during the early window', status => {
  const booking = {...base,dispatch:{status}};
  const early = new Date('2026-10-16T20:00:00Z');
  expect(decorate(booking,early).coordination.canCall).toBe(false);
  expect(() => checkCall(booking,early)).toThrow();
});

test('dispatcher can issue a call for a ready vehicle six hours before appointment', async () => {
  const booking = {...base,appointmentStart:new Date(Date.now()+6*60*60000).toISOString(),dispatch:{status:'ready',version:1}};
  sequelize.query.mockResolvedValueOnce([{id}]).mockResolvedValueOnce([booking])
    .mockResolvedValueOnce([]).mockResolvedValueOnce([]).mockResolvedValueOnce([{booking_id:id}])
    .mockResolvedValueOnce([{...booking,dispatch:{status:'called',version:2},gateEntryOrder:{status:'pending_ack'}}]);
  const updated = await service.act(id,'Dispatcher',id,'call',{expectedVersion:1});
  expect(updated.dispatch.status).toBe('called');
  expect(updated.gateEntryOrder.status).toBe('pending_ack');
  expect(sequelize.query.mock.calls[4][1].replacements).toMatchObject({status:'called',nextVersion:2});
  expect(admission.lock.mock.invocationCallOrder.at(-1)).toBeLessThan(sequelize.query.mock.invocationCallOrder[0]);
});

test.each([null, { status: 'expired' }, { status: 'active' }])('acknowledgment reports success only with a gate permission that became active (%j)', async gateEntryOrder => {
  const booking = { ...base, dispatch: { status: 'called', callExpiresAt: new Date(Date.now()+60000).toISOString(), version: 2 } };
  sequelize.query.mockResolvedValueOnce([{id}]).mockResolvedValueOnce([booking])
    .mockResolvedValueOnce([{booking_id:id}])
    .mockResolvedValueOnce([{...booking, dispatch: { status: 'acknowledged', version: 3 }, gateEntryOrder}]);
  const result = service.act(id, 'Driver', id, 'acknowledge', { expectedVersion: 2 });
  if (gateEntryOrder?.status === 'active') {
    await expect(result).resolves.toMatchObject({gateEntryOrder});
  } else {
    await expect(result).rejects.toMatchObject({statusCode:409});
  }
});

test('full capacity stops a manual call inside the transaction before an entry order is issued', async () => {
  const booking = {...base,appointmentStart:new Date().toISOString(),dispatch:{status:'ready',version:1}};
  sequelize.query.mockResolvedValueOnce([{id}]).mockResolvedValueOnce([booking]);
  admission.requireSpace.mockRejectedValueOnce(Object.assign(new Error('Bãi đã đủ sức chứa'),{statusCode:409}));
  await expect(service.act(id,'Dispatcher',id,'call',{expectedVersion:1})).rejects.toMatchObject({statusCode:409});
  expect(sequelize.query).toHaveBeenCalledTimes(2);
});
test('late classification uses arrival timestamp, not time spent waiting for the port', () => {
  expect(decorate({...base,dispatch:{status:'ready',arrivedAt:base.appointmentStart}},new Date('2026-10-17T05:00:00Z')).coordination.isLate).toBe(false);
  expect(decorate({...base,dispatch:{status:'ready',arrivedAt:'2026-10-17T03:11:00Z'}},now).coordination.isLate).toBe(true);
});
test('expired calls cannot be acknowledged, including before the expiry worker runs', () => {
  expect(decorate({...base,dispatch:{status:'called',callExpiresAt:now.toISOString()}},now).coordination.canAcknowledge).toBe(false);
});
test('wrong driver ownership fails without any state writes', async () => {
  sequelize.query.mockResolvedValueOnce([]);
  await expect(service.act(id,'Driver',id,'arrive',{expectedVersion:0})).rejects.toMatchObject({statusCode:404});
  expect(sequelize.query).toHaveBeenCalledTimes(1);
  expect(sequelize.query.mock.calls[0][0]).toContain('d.user_id=:userId');
});
test('stale dispatcher versions fail before a duplicate command or notification is written', async () => {
  sequelize.query.mockResolvedValueOnce([{id}]).mockResolvedValueOnce([{...base,dispatch:{status:'ready',version:3}}]);
  await expect(service.act(id,'Dispatcher',id,'call',{expectedVersion:2})).rejects.toMatchObject({statusCode:409});
  expect(sequelize.query).toHaveBeenCalledTimes(2);
});
test('missing expected version is rejected before accessing database', async () => {
  await expect(service.act(id,'Driver',id,'arrive',{})).rejects.toMatchObject({statusCode:422});
  expect(sequelize.query).not.toHaveBeenCalled();
});
test('delay needs a reason and bounded duration', async () => {
  sequelize.query.mockResolvedValueOnce([{id}]).mockResolvedValueOnce([{...base,dispatch:{status:'ready',version:1}}]);
  await expect(service.act(id,'Dispatcher',id,'delay',{expectedVersion:1,minutes:999,note:''})).rejects.toMatchObject({statusCode:422});
  expect(sequelize.query).toHaveBeenCalledTimes(2);
});
test('arrival persists separate dispatch state, without changing booking or gate status', async () => {
  const booking={...base,appointmentStart:new Date(Date.now()-60000).toISOString()};
  sequelize.query.mockResolvedValueOnce([{id}]).mockResolvedValueOnce([booking])
    .mockResolvedValueOnce([{booking_id:id}]).mockResolvedValueOnce([{...booking,dispatch:{status:'ready',version:1}}]);
  const updated=await service.act(id,'Driver',id,'arrive',{expectedVersion:0});
  expect(updated.dispatch.status).toBe('ready');
  expect(sequelize.query.mock.calls[2][0]).toContain('INSERT INTO booking_dispatch_state');
  for(const [sql,options] of sequelize.query.mock.calls){
    expect(sql).not.toMatch(/UPDATE bookings|INSERT INTO gate_transactions/i);
    expect(options.transaction).toBeDefined();
  }
});
test.each([
  ['Driver','get','/board?date=2026-10-17'],
  ['Driver','post','/'+id+'/call'],
  ['Dispatcher','post','/'+id+'/arrive'],
  ['Transport Company','post','/'+id+'/call'],
])('%s cannot %s %s',async(role,method,path)=>{
  const response=await request(app)[method]('/dispatch'+path).set('Authorization','Bearer '+token(role)).send({expectedVersion:0});
  expect(response.status).toBe(403);
  expect(sequelize.query).not.toHaveBeenCalled();
});
test('unauthenticated board is rejected',async()=>{
  expect((await request(app).get('/dispatch/board?date=2026-10-17')).status).toBe(401);
});
test('board requests only a page of three and returns counts for the full queue', async () => {
  const snapshot = { page: 2, total: 5, ready: 2, absent: 3, called: 0, inside: 5, occupied: 5,
    bookings: [{ ...base, group: 'inside' }, { ...base, id: 'another-booking', group: 'inside' }] };
  sequelize.query.mockResolvedValueOnce([snapshot]);
  const response = await request(app).get('/dispatch/board?date=2026-10-17&filter=inside&page=2')
    .set('Authorization', 'Bearer ' + token('Dispatcher'));
  expect(response.status).toBe(200);
  expect(response.body.data).toMatchObject({ counts: { ready: 2, absent: 3, called: 0 },
    pagination: { page: 2, pageSize: 3, total: 5 }, yard: { inside: 5 } });
  expect(response.body.data.bookings).toHaveLength(2);
  expect(sequelize.query).toHaveBeenCalledTimes(1);
  expect(sequelize.query.mock.calls[0][1].replacements).toMatchObject({ page: 2, filter: 'inside', size: 3 });
});
test('invalid pagination and filters are rejected before querying', async () => {
  for (const query of ['page=0', 'page=1&page=2', 'filter=missing']) {
    const response = await request(app).get('/dispatch/board?date=2026-10-17&' + query)
      .set('Authorization', 'Bearer ' + token('Dispatcher'));
    expect(response.status).toBe(422);
  }
  expect(sequelize.query).not.toHaveBeenCalled();
});
test('dispatcher can load a linked booking outside the current page', async () => {
  sequelize.query.mockResolvedValueOnce([{ ...base, group: 'inside', bookingCode: 'BK-LOOKUP' }]);
  const response = await request(app).get('/dispatch/board/booking?bookingCode=bk-lookup')
    .set('Authorization', 'Bearer ' + token('Dispatcher'));
  expect(response.body.data).toMatchObject({ id, bookingCode: 'BK-LOOKUP', group: 'inside' });
  expect(sequelize.query.mock.calls[0][1].replacements.reference).toBe('BK-LOOKUP');
});
test('transport accounts can open queue details scoped to the selected date', async () => {
  sequelize.query.mockResolvedValueOnce([{ ...base, group: 'queue', bookingCode: 'BK-LOOKUP' }]);
  const response = await request(app).get('/dispatch/board/booking?date=2026-10-17&bookingCode=bk-lookup')
    .set('Authorization', 'Bearer ' + token('Transport Company'));
  expect(response.body.data).toMatchObject({ id, group: 'queue' });
  expect(sequelize.query.mock.calls[0][0]).not.toContain('gate_transactions');
  expect(sequelize.query.mock.calls[0][1].replacements.date).toBe('2026-10-17');
});
test('invalid booking ID is rejected before the service',async()=>{
  expect((await request(app).post('/dispatch/not-a-uuid/arrive').set('Authorization','Bearer '+token('Driver')).send({expectedVersion:0})).status).toBe(422);
});

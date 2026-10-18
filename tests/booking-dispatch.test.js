'use strict';
jest.mock('../src/config/database', () => ({
  sequelize: { query: jest.fn(), transaction: jest.fn(fn => fn({ id: 'test-transaction' })) },
}));
jest.mock('../src/models/User', () => ({ User: { findByPk: jest.fn() } }));
jest.mock('../src/modules/booking-dispatch/dispatch.realtime', () => ({ pumpDispatchEvents: jest.fn() }));
jest.mock('../src/modules/booking-dispatch/yard-admission', () => ({
  lock: jest.fn(), requireSpace: jest.fn(), assignDestination: jest.fn(async () => true), snapshot: jest.fn(),
}));
const express = require('express');
const request = require('supertest');
const { sequelize } = require('../src/config/database');
const { User } = require('../src/models/User');
const { generateToken } = require('../src/utils/jwt');
const service = require('../src/modules/booking-dispatch/dispatch.service');
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
    .mockResolvedValueOnce([{...booking,dispatch:{status:'called',version:2}}]);
  const updated = await service.act(id,'Dispatcher',id,'call',{expectedVersion:1});
  expect(updated.dispatch.status).toBe('called');
  expect(sequelize.query.mock.calls[4][1].replacements).toMatchObject({status:'called',nextVersion:2});
  expect(admission.lock.mock.invocationCallOrder.at(-1)).toBeLessThan(sequelize.query.mock.invocationCallOrder[0]);
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
test('invalid booking ID is rejected before the service',async()=>{
  expect((await request(app).post('/dispatch/not-a-uuid/arrive').set('Authorization','Bearer '+token('Driver')).send({expectedVersion:0})).status).toBe(422);
});

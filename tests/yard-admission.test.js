'use strict';
jest.mock('../src/config/database', () => ({ sequelize: { query: jest.fn(), transaction: jest.fn() } }));
jest.mock('../src/modules/booking-dispatch/dispatch.service', () => ({ act: jest.fn() }));
const { sequelize } = require('../src/config/database');
const { act } = require('../src/modules/booking-dispatch/dispatch.service');
const admission = require('../src/modules/booking-dispatch/yard-admission');
beforeEach(() => {
  jest.clearAllMocks(); sequelize.query.mockReset();
  delete process.env.YARD_VEHICLE_CAPACITY; delete process.env.YARD_AUTO_ADMISSION;
});
afterEach(() => { delete process.env.YARD_VEHICLE_CAPACITY; delete process.env.YARD_AUTO_ADMISSION; });
test('capacity counts vehicles plus outstanding invitations, with no double-count at Gate In', async () => {
  sequelize.query.mockResolvedValueOnce([{ inside: 4, occupied: 6 }]).mockResolvedValueOnce([{ inside: 5, occupied: 6 }]);
  expect(await admission.snapshot()).toMatchObject({ capacity: 6, inside: 4, reserved: 2, available: 0 });
  expect(await admission.snapshot()).toMatchObject({ inside: 5, reserved: 1, available: 0 });
  expect(sequelize.query.mock.calls[0][0]).toContain('SELECT booking_id FROM inside UNION SELECT booking_id FROM reserved');
  expect(sequelize.query.mock.calls[0][0]).toContain("e.valid_until>now()");
});
test('full yard rejects admission even if a caller bypasses the disabled button', async () => {
  sequelize.query.mockResolvedValue([{ inside: 6, occupied: 6 }]);
  await expect(admission.requireSpace({})).rejects.toMatchObject({ statusCode: 409 });
  expect(act).not.toHaveBeenCalled();
});
test('invalid capacity defaults to six and over-capacity never advertises negative places', async () => {
  process.env.YARD_VEHICLE_CAPACITY = '-1';
  sequelize.query.mockResolvedValue([{ inside: 8, occupied: 9 }]);
  expect(await admission.snapshot()).toMatchObject({ capacity: 6, available: 0 });
});
test('automatic calls stop after the last available place and use current dispatch version', async () => {
  sequelize.query.mockResolvedValueOnce([]).mockResolvedValueOnce([{ inside: 5, occupied: 5 }])
    .mockResolvedValueOnce([{ id: 'next', version: 4 }, { id: 'later', version: 7 }])
    .mockResolvedValueOnce([{ inside: 5, occupied: 6 }]);
  act.mockResolvedValue({});
  await admission.runAutoAdmission();
  expect(act).toHaveBeenCalledTimes(1);
  expect(act).toHaveBeenCalledWith(null, 'Dispatcher', 'next', 'call', expect.objectContaining({ expectedVersion: 4 }));
  expect(sequelize.query.mock.calls[2][0]).toContain("'call_expired','entry_expired','released'");
});
test('automatic admission can be disabled without queries or calls', async () => {
  process.env.YARD_AUTO_ADMISSION = 'false';
  await admission.runAutoAdmission();
  expect(sequelize.query).not.toHaveBeenCalled();
});
test('assignment requires an approved reception point and cannot move physical cargo', async () => {
  sequelize.query.mockResolvedValueOnce([]);
  expect(await admission.assignDestination('booking', {})).toBe(false);
  expect(sequelize.query).toHaveBeenCalledTimes(1);
  sequelize.query.mockReset().mockResolvedValueOnce([{ location: 'A-S1' }]).mockResolvedValueOnce([]);
  expect(await admission.assignDestination('booking', {})).toBe(true);
  const sql = sequelize.query.mock.calls[1][0];
  expect(sql).toContain('INSERT INTO driver_route_instructions');
  expect(sql).toContain('NOT EXISTS');
  expect(sql).not.toMatch(/UPDATE containers|INSERT INTO yard_tasks|UPDATE bookings|INSERT INTO container_positions/);
});

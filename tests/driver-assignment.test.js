'use strict';

jest.mock('../src/config/database', () => ({ sequelize: { query: jest.fn() } }));
const { sequelize } = require('../src/config/database');
const { assignDriverProfile, getUserDriverAssignments } = require('../src/modules/users/driver-assignment.service');
const userId = 'e738b67f-dba6-4f5e-895f-eb2e81afe3bc';
const carrierId = 'c1010101-0000-0000-0000-000000000001';
const driverId = '13b9afa1-4205-46fa-896f-fe492da28f06';
const transaction = {};

beforeEach(() => sequelize.query.mockReset());

test('Driver account requires a valid company and driver profile', async () => {
  await expect(assignDriverProfile(userId, {}, transaction)).rejects.toMatchObject({ statusCode: 422 });
  expect(sequelize.query).not.toHaveBeenCalled();
});

test('a driver profile from a different company is rejected before writing', async () => {
  sequelize.query.mockResolvedValueOnce([{ id: driverId, carrier_id: 'different-company', user_id: null }]);
  await expect(assignDriverProfile(userId, { carrierId, driverId }, transaction)).rejects.toMatchObject({ statusCode: 422 });
  expect(sequelize.query).toHaveBeenCalledTimes(1);
});

test('a profile already assigned to another account cannot be taken', async () => {
  sequelize.query.mockResolvedValueOnce([{ id: driverId, carrier_id: carrierId, user_id: 'another-user' }]);
  await expect(assignDriverProfile(userId, { carrierId, driverId }, transaction)).rejects.toMatchObject({ statusCode: 409 });
  expect(sequelize.query).toHaveBeenCalledTimes(1);
});

test('valid assignment and booking notifications use the account transaction', async () => {
  sequelize.query.mockResolvedValueOnce([{ id: driverId, carrier_id: carrierId, user_id: null }]);
  sequelize.query.mockResolvedValue([]);
  await assignDriverProfile(userId, { carrierId, driverId }, transaction);
  expect(sequelize.query).toHaveBeenCalledTimes(5);
  for (const [, options] of sequelize.query.mock.calls) expect(options.transaction).toBe(transaction);
});

test('no driver accounts means no extra lookup', async () => {
  expect(await getUserDriverAssignments([])).toEqual([]);
  expect(sequelize.query).not.toHaveBeenCalled();
});

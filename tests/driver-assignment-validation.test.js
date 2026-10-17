'use strict';

jest.mock('../src/models/User', () => ({ VALID_ROLES: ['Administrator', 'Driver', 'Dispatcher'] }));
jest.mock('../src/modules/users/users.service', () => ({
  createUser: jest.fn(), updateUser: jest.fn(), assignRole: jest.fn(),
}));

const express = require('express');
const request = require('supertest');
const service = require('../src/modules/users/users.service');
const validators = require('../src/modules/users/users.validator');
const controller = require('../src/modules/users/users.controller');
const app = express();
app.use(express.json());
app.post('/users', validators.createUserValidator, controller.createUser);
app.put('/users/:id', validators.updateUserValidator, controller.updateUser);
app.patch('/users/:id/role', validators.assignRoleValidator, controller.assignRole);
app.use((error, _req, res, _next) => res.status(500).json({ message: error.message }));

const carrierId = 'c1010101-0000-0000-0000-000000000001';
const driverId = '13b9afa1-4205-46fa-896f-fe492da28f06';
const payload = { username: 'driver-regression', email: 'driver@example.invalid', password: 'test-password', role: 'Driver', carrierId, driverId };

beforeEach(() => {
  jest.clearAllMocks();
  service.createUser.mockResolvedValue({ id: 'account', carrierId, driverId });
  service.updateUser.mockResolvedValue({ id: 'account', carrierId, driverId });
  service.assignRole.mockResolvedValue({ id: 'account', carrierId, driverId });
});

test('creating a Driver accepts the real company GUID returned by assignment-options', async () => {
  const response = await request(app).post('/users').send(payload);
  expect(response.status).toBe(201);
  expect(service.createUser).toHaveBeenCalledWith(expect.objectContaining({ carrierId, driverId, role: 'Driver' }));
});

test('edit and role assignment use the same database GUID contract', async () => {
  expect((await request(app).put('/users/account').send({ carrierId, driverId })).status).toBe(200);
  expect((await request(app).patch('/users/account/role').send({ role: 'Driver', carrierId, driverId })).status).toBe(200);
  expect(service.assignRole).toHaveBeenCalledWith('account', 'Driver', { carrierId, driverId });
});

test('IDs are normalized before the membership check', async () => {
  expect((await request(app).post('/users').send({ ...payload, carrierId: ` ${carrierId.toUpperCase()} ` })).status).toBe(201);
  expect(service.createUser).toHaveBeenCalledWith(expect.objectContaining({ carrierId }));
});

test.each(['not-a-guid', "';SELECT 1;--", '', null])('malformed company ID %s is rejected before account writes', async invalid => {
  expect((await request(app).post('/users').send({ ...payload, carrierId: invalid })).status).toBe(422);
  expect(service.createUser).not.toHaveBeenCalled();
});

test('missing Driver profile and invalid email have separate actionable errors', async () => {
  const missing = await request(app).post('/users').send({ ...payload, driverId: undefined });
  expect(missing.status).toBe(422);
  expect(missing.body.errors.some(error => error.field === 'driverId')).toBe(true);
  const email = await request(app).post('/users').send({ ...payload, email: 'dispatcher01' });
  expect(email.status).toBe(422);
  expect(email.body.errors).toEqual(expect.arrayContaining([expect.objectContaining({ field: 'email', message: 'Email không hợp lệ.' })]));
  expect(service.createUser).not.toHaveBeenCalled();
});

test('company membership and profile ownership failures remain errors', async () => {
  service.createUser.mockRejectedValueOnce(Object.assign(new Error('Hồ sơ tài xế không thuộc công ty vận tải đã chọn.'), { statusCode: 422 }));
  expect((await request(app).post('/users').send(payload)).status).toBe(422);
  service.createUser.mockRejectedValueOnce(Object.assign(new Error('Hồ sơ tài xế đã được cấp cho tài khoản khác.'), { statusCode: 409 }));
  expect((await request(app).post('/users').send(payload)).status).toBe(409);
});

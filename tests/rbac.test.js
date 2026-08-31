'use strict';

/**
 * Integration tests — Role-Based Authorization (RBAC)
 * Stack: Jest + Supertest
 *
 * Strategy:
 *   1. Tạo 7 users, mỗi user thuộc 1 role.
 *   2. Login từng user lấy JWT token.
 *   3. Gọi từng protected endpoint với token của từng role.
 *   4. Kiểm tra response: 200 (allowed) hoặc 403 (denied) hoặc 401 (no token).
 *
 * Matrix kiểm thử:
 *
 *  Endpoint                | Admin | Dispatcher | Gate | Yard | Berth | Carrier | Driver
 *  ----------------------- |-------|------------|------|------|-------|---------|-------
 *  GET /admin/users        |  200  |    403     | 403  | 403  |  403  |   403   |  403
 *  GET /admin/dashboard    |  200  |    200     | 403  | 403  |  403  |   403   |  403
 *  GET /dispatcher/ops     |  200  |    200     | 403  | 403  |  403  |   403   |  403
 *  GET /gate/check-in      |  200  |    403     | 200  | 403  |  403  |   403   |  403
 *  GET /yard/containers    |  200  |    403     | 403  | 200  |  403  |   403   |  403
 *  GET /berth/schedule     |  200  |    403     | 403  | 403  |  200  |   403   |  403
 *  GET /transport/bookings |  200  |    200     | 403  | 403  |  403  |   200   |  403
 *  GET /driver/trips       |  200  |    403     | 403  | 403  |  403  |   403   |  200
 *  GET /profile/me         |  200  |    200     | 200  | 200  |  200  |   200   |  200
 */

process.env.NODE_ENV     = 'test';
process.env.PORT         = '3003';
process.env.DB_HOST      = process.env.DB_HOST     || 'localhost';
process.env.DB_PORT      = process.env.DB_PORT     || '5432';
process.env.DB_NAME      = process.env.DB_NAME     || 'nexusport_test';
process.env.DB_USER      = process.env.DB_USER     || 'postgres';
process.env.DB_PASSWORD  = process.env.DB_PASSWORD || 'postgres';
process.env.JWT_SECRET   = 'test_jwt_secret_key_minimum_32_chars_ok';
process.env.JWT_EXPIRES_IN      = '1h';
process.env.BCRYPT_SALT_ROUNDS  = '4';

const request = require('supertest');
const bcrypt  = require('bcryptjs');
const app     = require('../src/app');
const { syncDB, sequelize } = require('../src/config/database');
const { User } = require('../src/models/User');

// ─── Setup ───────────────────────────────────────────────────────────────────

const TEST_PASSWORD = 'NexusPort@2026';
const ROLES = [
  { key: 'admin',     role: 'Administrator',     username: 'rbac_admin'    },
  { key: 'dispatcher',role: 'Dispatcher',         username: 'rbac_dispatch' },
  { key: 'gate',      role: 'Gate Officer',       username: 'rbac_gate'     },
  { key: 'yard',      role: 'Yard Operator',      username: 'rbac_yard'     },
  { key: 'berth',     role: 'Berth Staff',        username: 'rbac_berth'    },
  { key: 'carrier',   role: 'Transport Company',  username: 'rbac_carrier'  },
  { key: 'driver',    role: 'Driver',             username: 'rbac_driver'   },
];

// Tokens lưu sau khi login từng role
const tokens = {};

beforeAll(async () => {
  await syncDB({ force: true });

  const hashedPassword = await bcrypt.hash(TEST_PASSWORD, 4);

  // Tạo user cho tất cả roles
  for (const { role, username } of ROLES) {
    await User.create({
      username,
      email:     `${username}@nexusport.vn`,
      password:  hashedPassword,
      role,
      full_name: `Test ${role}`,
      is_active: true,
    });
  }

  // Login từng user lấy token
  for (const { key, username } of ROLES) {
    const res = await request(app)
      .post('/api/auth/login')
      .send({ username, password: TEST_PASSWORD });

    expect(res.status).toBe(200);
    tokens[key] = res.body.data.token;
  }
});

afterAll(async () => {
  await sequelize.close();
});

// ─── Helper ──────────────────────────────────────────────────────────────────
const get = (endpoint, tokenKey) =>
  request(app)
    .get(endpoint)
    .set('Authorization', `Bearer ${tokens[tokenKey]}`);

// ─── Test Suites ─────────────────────────────────────────────────────────────

// ─── 1. GET /api/admin/users ─────────────────────────────────────────────────
describe('GET /api/admin/users — Administrator only', () => {
  it('Administrator → 200', () => get('/api/admin/users', 'admin').expect(200));
  it('Dispatcher → 403',    () => get('/api/admin/users', 'dispatcher').expect(403));
  it('Gate Officer → 403',  () => get('/api/admin/users', 'gate').expect(403));
  it('Yard Operator → 403', () => get('/api/admin/users', 'yard').expect(403));
  it('Berth Staff → 403',   () => get('/api/admin/users', 'berth').expect(403));
  it('Transport Company → 403', () => get('/api/admin/users', 'carrier').expect(403));
  it('Driver → 403',        () => get('/api/admin/users', 'driver').expect(403));
  it('No token → 401', () =>
    request(app).get('/api/admin/users').expect(401));
});

// ─── 2. GET /api/admin/dashboard ─────────────────────────────────────────────
describe('GET /api/admin/dashboard — Administrator + Dispatcher', () => {
  it('Administrator → 200',  () => get('/api/admin/dashboard', 'admin').expect(200));
  it('Dispatcher → 200',     () => get('/api/admin/dashboard', 'dispatcher').expect(200));
  it('Gate Officer → 403',   () => get('/api/admin/dashboard', 'gate').expect(403));
  it('Yard Operator → 403',  () => get('/api/admin/dashboard', 'yard').expect(403));
  it('Berth Staff → 403',    () => get('/api/admin/dashboard', 'berth').expect(403));
  it('Transport Company → 403', () => get('/api/admin/dashboard', 'carrier').expect(403));
  it('Driver → 403',         () => get('/api/admin/dashboard', 'driver').expect(403));
  it('No token → 401', () =>
    request(app).get('/api/admin/dashboard').expect(401));
});

// ─── 3. GET /api/dispatcher/operations ───────────────────────────────────────
describe('GET /api/dispatcher/operations — Administrator + Dispatcher', () => {
  it('Administrator → 200',  () => get('/api/dispatcher/operations', 'admin').expect(200));
  it('Dispatcher → 200',     () => get('/api/dispatcher/operations', 'dispatcher').expect(200));
  it('Gate Officer → 403',   () => get('/api/dispatcher/operations', 'gate').expect(403));
  it('Yard Operator → 403',  () => get('/api/dispatcher/operations', 'yard').expect(403));
  it('Berth Staff → 403',    () => get('/api/dispatcher/operations', 'berth').expect(403));
  it('Transport Company → 403', () => get('/api/dispatcher/operations', 'carrier').expect(403));
  it('Driver → 403',         () => get('/api/dispatcher/operations', 'driver').expect(403));
});

// ─── 4. GET /api/gate/check-in ───────────────────────────────────────────────
describe('GET /api/gate/check-in — Administrator + Gate Officer', () => {
  it('Administrator → 200',  () => get('/api/gate/check-in', 'admin').expect(200));
  it('Dispatcher → 403',     () => get('/api/gate/check-in', 'dispatcher').expect(403));
  it('Gate Officer → 200',   () => get('/api/gate/check-in', 'gate').expect(200));
  it('Yard Operator → 403',  () => get('/api/gate/check-in', 'yard').expect(403));
  it('Berth Staff → 403',    () => get('/api/gate/check-in', 'berth').expect(403));
  it('Transport Company → 403', () => get('/api/gate/check-in', 'carrier').expect(403));
  it('Driver → 403',         () => get('/api/gate/check-in', 'driver').expect(403));
});

// ─── 5. GET /api/yard/containers ─────────────────────────────────────────────
describe('GET /api/yard/containers — Administrator + Yard Operator', () => {
  it('Administrator → 200',  () => get('/api/yard/containers', 'admin').expect(200));
  it('Dispatcher → 403',     () => get('/api/yard/containers', 'dispatcher').expect(403));
  it('Gate Officer → 403',   () => get('/api/yard/containers', 'gate').expect(403));
  it('Yard Operator → 200',  () => get('/api/yard/containers', 'yard').expect(200));
  it('Berth Staff → 403',    () => get('/api/yard/containers', 'berth').expect(403));
  it('Transport Company → 403', () => get('/api/yard/containers', 'carrier').expect(403));
  it('Driver → 403',         () => get('/api/yard/containers', 'driver').expect(403));
});

// ─── 6. GET /api/berth/schedule ──────────────────────────────────────────────
describe('GET /api/berth/schedule — Administrator + Berth Staff', () => {
  it('Administrator → 200',  () => get('/api/berth/schedule', 'admin').expect(200));
  it('Dispatcher → 403',     () => get('/api/berth/schedule', 'dispatcher').expect(403));
  it('Gate Officer → 403',   () => get('/api/berth/schedule', 'gate').expect(403));
  it('Yard Operator → 403',  () => get('/api/berth/schedule', 'yard').expect(403));
  it('Berth Staff → 200',    () => get('/api/berth/schedule', 'berth').expect(200));
  it('Transport Company → 403', () => get('/api/berth/schedule', 'carrier').expect(403));
  it('Driver → 403',         () => get('/api/berth/schedule', 'driver').expect(403));
});

// ─── 7. GET /api/transport/bookings ──────────────────────────────────────────
describe('GET /api/transport/bookings — Administrator + Transport Company + Dispatcher', () => {
  it('Administrator → 200',  () => get('/api/transport/bookings', 'admin').expect(200));
  it('Dispatcher → 200',     () => get('/api/transport/bookings', 'dispatcher').expect(200));
  it('Gate Officer → 403',   () => get('/api/transport/bookings', 'gate').expect(403));
  it('Yard Operator → 403',  () => get('/api/transport/bookings', 'yard').expect(403));
  it('Berth Staff → 403',    () => get('/api/transport/bookings', 'berth').expect(403));
  it('Transport Company → 200', () => get('/api/transport/bookings', 'carrier').expect(200));
  it('Driver → 403',         () => get('/api/transport/bookings', 'driver').expect(403));
});

// ─── 8. GET /api/driver/trips ────────────────────────────────────────────────
describe('GET /api/driver/trips — Administrator + Driver', () => {
  it('Administrator → 200',  () => get('/api/driver/trips', 'admin').expect(200));
  it('Dispatcher → 403',     () => get('/api/driver/trips', 'dispatcher').expect(403));
  it('Gate Officer → 403',   () => get('/api/driver/trips', 'gate').expect(403));
  it('Yard Operator → 403',  () => get('/api/driver/trips', 'yard').expect(403));
  it('Berth Staff → 403',    () => get('/api/driver/trips', 'berth').expect(403));
  it('Transport Company → 403', () => get('/api/driver/trips', 'carrier').expect(403));
  it('Driver → 200',         () => get('/api/driver/trips', 'driver').expect(200));
});

// ─── 9. GET /api/profile/me ──────────────────────────────────────────────────
describe('GET /api/profile/me — All authenticated roles', () => {
  it('Administrator → 200',  () => get('/api/profile/me', 'admin').expect(200));
  it('Dispatcher → 200',     () => get('/api/profile/me', 'dispatcher').expect(200));
  it('Gate Officer → 200',   () => get('/api/profile/me', 'gate').expect(200));
  it('Yard Operator → 200',  () => get('/api/profile/me', 'yard').expect(200));
  it('Berth Staff → 200',    () => get('/api/profile/me', 'berth').expect(200));
  it('Transport Company → 200', () => get('/api/profile/me', 'carrier').expect(200));
  it('Driver → 200',         () => get('/api/profile/me', 'driver').expect(200));
  it('No token → 401', () =>
    request(app).get('/api/profile/me').expect(401));
});

// ─── 10. Response body ────────────────────────────────────────────────────────
describe('Response body structure', () => {
  it('200 response có success:true và endpoint info', async () => {
    const res = await get('/api/admin/users', 'admin');
    expect(res.body.success).toBe(true);
    expect(res.body.user.role).toBe('Administrator');
    expect(res.body.endpoint).toBeDefined();
  });

  it('403 response có success:false, yourRole và requiredRoles', async () => {
    const res = await get('/api/admin/users', 'driver');
    expect(res.body.success).toBe(false);
    expect(res.body.detail.yourRole).toBe('Driver');
    expect(Array.isArray(res.body.detail.requiredRoles)).toBe(true);
    expect(res.body.detail.requiredRoles).toContain('Administrator');
  });

  it('401 response khi không có token', async () => {
    const res = await request(app).get('/api/admin/users');
    expect(res.status).toBe(401);
    expect(res.body.success).toBe(false);
  });
});

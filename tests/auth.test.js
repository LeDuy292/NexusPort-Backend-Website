'use strict';

/**
 * Integration tests cho Auth module
 * Stack: Jest + Supertest
 *
 * Test coverage:
 *   POST /api/auth/login
 *     ✅ Login thành công → 200 + JWT
 *     ✅ Login bằng email → 200 + JWT
 *     ✅ Sai password → 401
 *     ✅ Username không tồn tại → 401
 *     ✅ Thiếu username → 422
 *     ✅ Thiếu password → 422
 *     ✅ Account inactive → 403
 *   GET /api/auth/me
 *     ✅ Token hợp lệ → 200 + user info
 *     ✅ Không có token → 401
 *     ✅ Token sai → 401
 *   GET /api/health
 *     ✅ Health check → 200
 */

// Đặt TEST env trước khi load bất kỳ module nào
process.env.NODE_ENV = 'test';
process.env.PORT = '3002';
process.env.DB_HOST = process.env.DB_HOST || 'localhost';
process.env.DB_PORT = process.env.DB_PORT || '5432';
process.env.DB_NAME = process.env.DB_NAME || 'nexusport_test';
process.env.DB_USER = process.env.DB_USER || 'postgres';
process.env.DB_PASSWORD = process.env.DB_PASSWORD || 'postgres';
process.env.JWT_SECRET = 'test_jwt_secret_key_minimum_32_chars_ok';
process.env.JWT_EXPIRES_IN = '1h';
process.env.BCRYPT_SALT_ROUNDS = '4'; // Thấp để test nhanh

const request = require('supertest');
const bcrypt = require('bcryptjs');
const app = require('../src/app');
const { syncDB, sequelize } = require('../src/config/database');
const { User } = require('../src/models/User');

// ─── Setup & Teardown ────────────────────────────────────────────────────────

let testUser;
let inactiveUser;

beforeAll(async () => {
  // Sync DB với force:true để tạo bảng sạch cho test
  await syncDB({ force: true });

  const hashedPassword = await bcrypt.hash('NexusPort@2026', 4);

  // Tạo user test hợp lệ
  testUser = await User.create({
    username: 'testdispatcher',
    email: 'testdispatcher@nexusport.vn',
    password: hashedPassword,
    role: 'Dispatcher',
    full_name: 'Test Dispatcher',
    is_active: true,
  });

  // Tạo user bị vô hiệu hóa
  inactiveUser = await User.create({
    username: 'inactiveuser',
    email: 'inactive@nexusport.vn',
    password: hashedPassword,
    role: 'Gate Officer',
    full_name: 'Inactive User',
    is_active: false,
  });
});

afterAll(async () => {
  await sequelize.close();
});

// ─── Helper ─────────────────────────────────────────────────────────────────
const loginRequest = (body) => request(app).post('/api/auth/login').send(body);

// ─── POST /api/auth/login ────────────────────────────────────────────────────
describe('POST /api/auth/login', () => {
  describe('✅ Trường hợp thành công', () => {
    it('Login bằng username hợp lệ → 200 + JWT + user info', async () => {
      const res = await loginRequest({
        username: 'testdispatcher',
        password: 'NexusPort@2026',
      });

      expect(res.status).toBe(200);
      expect(res.body.success).toBe(true);
      expect(res.body.message).toBe('Đăng nhập thành công.');
      expect(res.body.data).toHaveProperty('token');
      expect(res.body.data.token).toBeTruthy();
      expect(res.body.data.user).toMatchObject({
        username: 'testdispatcher',
        email: 'testdispatcher@nexusport.vn',
        role: 'Dispatcher',
        isActive: true,
      });
      // Password KHÔNG được xuất hiện trong response
      expect(res.body.data.user).not.toHaveProperty('password');
    });

    it('Login bằng email hợp lệ → 200 + JWT', async () => {
      const res = await loginRequest({
        username: 'testdispatcher@nexusport.vn',
        password: 'NexusPort@2026',
      });

      expect(res.status).toBe(200);
      expect(res.body.data).toHaveProperty('token');
    });

    it('Token nhận được phải là JWT hợp lệ (3 phần ngăn cách bởi dấu chấm)', async () => {
      const res = await loginRequest({
        username: 'testdispatcher',
        password: 'NexusPort@2026',
      });

      const token = res.body.data.token;
      const parts = token.split('.');
      expect(parts).toHaveLength(3);
    });
  });

  describe('❌ Thông tin đăng nhập sai', () => {
    it('Sai password → 401', async () => {
      const res = await loginRequest({
        username: 'testdispatcher',
        password: 'WrongPassword123',
      });

      expect(res.status).toBe(401);
      expect(res.body.success).toBe(false);
      expect(res.body.message).toBe('Mật khẩu không chính xác!');
    });

    it('Username không tồn tại → 401', async () => {
      const res = await loginRequest({
        username: 'nonexistentuser',
        password: 'AnyPassword123',
      });

      expect(res.status).toBe(401);
      expect(res.body.success).toBe(false);
      expect(res.body.message).toBe('Tài khoản không tồn tại!');
    });
  });

  describe('🚫 Tài khoản bị vô hiệu hóa', () => {
    it('Account inactive → 403', async () => {
      const res = await loginRequest({
        username: 'inactiveuser',
        password: 'NexusPort@2026',
      });

      expect(res.status).toBe(403);
      expect(res.body.success).toBe(false);
      expect(res.body.message).toContain('vô hiệu hóa');
    });
  });

  describe('⚠️ Validation', () => {
    it('Thiếu username → 422', async () => {
      const res = await loginRequest({ password: 'NexusPort@2026' });

      expect(res.status).toBe(422);
      expect(res.body.success).toBe(false);
      expect(res.body.errors).toBeDefined();
    });

    it('Thiếu password → 422', async () => {
      const res = await loginRequest({ username: 'testdispatcher' });

      expect(res.status).toBe(422);
      expect(res.body.success).toBe(false);
      expect(res.body.errors).toBeDefined();
    });

    it('Body rỗng → 422', async () => {
      const res = await loginRequest({});

      expect(res.status).toBe(422);
      expect(res.body.success).toBe(false);
    });
  });
});

// ─── GET /api/auth/me ────────────────────────────────────────────────────────
describe('GET /api/auth/me', () => {
  let validToken;

  beforeAll(async () => {
    const res = await loginRequest({
      username: 'testdispatcher',
      password: 'NexusPort@2026',
    });
    validToken = res.body.data.token;
  });

  it('Token hợp lệ → 200 + thông tin user', async () => {
    const res = await request(app)
      .get('/api/auth/me')
      .set('Authorization', `Bearer ${validToken}`);

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.user).toMatchObject({
      username: 'testdispatcher',
      role: 'Dispatcher',
    });
    expect(res.body.data.user).not.toHaveProperty('password');
  });

  it('Không có token → 401', async () => {
    const res = await request(app).get('/api/auth/me');

    expect(res.status).toBe(401);
    expect(res.body.success).toBe(false);
  });

  it('Token giả mạo → 401', async () => {
    const res = await request(app)
      .get('/api/auth/me')
      .set('Authorization', 'Bearer this.is.a.fake.token');

    expect(res.status).toBe(401);
    expect(res.body.success).toBe(false);
  });

  it('Authorization header sai format → 401', async () => {
    const res = await request(app)
      .get('/api/auth/me')
      .set('Authorization', validToken); // Thiếu "Bearer "

    expect(res.status).toBe(401);
  });
});

// ─── GET /api/health ─────────────────────────────────────────────────────────
describe('GET /api/health', () => {
  it('Health check → 200', async () => {
    const res = await request(app).get('/api/health');

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body).toHaveProperty('timestamp');
  });
});

// ─── 404 ─────────────────────────────────────────────────────────────────────
describe('404 Not Found', () => {
  it('Route không tồn tại → 404', async () => {
    const res = await request(app).get('/api/nonexistent');

    expect(res.status).toBe(404);
    expect(res.body.success).toBe(false);
  });
});

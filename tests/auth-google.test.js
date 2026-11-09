'use strict';

/**
 * Integration tests cho chức năng Đăng nhập bằng Google kèm Whitelist
 * POST /api/auth/google
 */

process.env.NODE_ENV = 'test';
process.env.PORT = '3003';
process.env.DB_HOST = process.env.DB_HOST || 'localhost';
process.env.DB_PORT = process.env.DB_PORT || '5432';
process.env.DB_NAME = process.env.DB_NAME || 'nexusport_test';
process.env.DB_USER = process.env.DB_USER || 'postgres';
process.env.DB_PASSWORD = process.env.DB_PASSWORD || 'postgres';
process.env.JWT_SECRET = 'test_jwt_secret_key_minimum_32_chars_ok';
process.env.JWT_EXPIRES_IN = '8h';
process.env.BCRYPT_SALT_ROUNDS = '4';

const request = require('supertest');
const bcrypt = require('bcryptjs');
const app = require('../src/app');
const { syncDB, sequelize } = require('../src/config/database');
const { User } = require('../src/models/User');

let driverUser;
let managerUser;
let inactiveGoogleUser;

beforeAll(async () => {
  await syncDB({ force: true });
  const hashedPassword = await bcrypt.hash('NexusPort@2026', 4);

  // 1. Tạo tài khoản Driver trong Whitelist
  driverUser = await User.create({
    username: 'driver_google01',
    email: 'driver01@gmail.com',
    password: hashedPassword,
    role: 'Driver',
    full_name: 'Nguyễn Văn A',
    is_active: true,
  });

  // 2. Tạo tài khoản Manager trong Whitelist
  managerUser = await User.create({
    username: 'manager_google01',
    email: 'manager01@gmail.com',
    password: hashedPassword,
    role: 'Administrator',
    full_name: 'Lê Văn C',
    is_active: true,
  });

  // 3. Tạo tài khoản trong Whitelist nhưng bị vô hiệu hóa
  inactiveGoogleUser = await User.create({
    username: 'inactive_google',
    email: 'disabled_user@gmail.com',
    password: hashedPassword,
    role: 'Gate Officer',
    full_name: 'Trần Văn Khóa',
    is_active: false,
  });
});

afterAll(async () => {
  await sequelize.close();
});

describe('POST /api/auth/google - Đăng nhập bằng Google & Whitelist', () => {
  it('1. Đăng nhập thành công với email hợp lệ trong Whitelist (Driver)', async () => {
    const res = await request(app)
      .post('/api/auth/google')
      .send({
        email: 'driver01@gmail.com',
      });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.token).toBeDefined();
    expect(res.body.data.user.email).toBe('driver01@gmail.com');
    expect(res.body.data.user.role).toBe('Driver');
    expect(res.body.data.user.fullName).toBe('Nguyễn Văn A');
  });

  it('2. Đăng nhập thành công với mock Google credential token', async () => {
    const res = await request(app)
      .post('/api/auth/google')
      .send({
        credential: 'mock-google-token',
        email: 'manager01@gmail.com',
      });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.token).toBeDefined();
    expect(res.body.data.user.email).toBe('manager01@gmail.com');
    expect(res.body.data.user.role).toBe('Administrator');
  });

  it('3. Từ chối email KHÔNG có trong danh sách Whitelist -> 403 EMAIL_NOT_WHITELISTED', async () => {
    const res = await request(app)
      .post('/api/auth/google')
      .send({
        email: 'stranger_unauthorized@gmail.com',
      });

    expect(res.status).toBe(403);
    expect(res.body.success).toBe(false);
    expect(res.body.code).toBe('EMAIL_NOT_WHITELISTED');
    expect(res.body.message).toContain('chưa được cấp phép truy cập vào hệ thống NexusPort');
  });

  it('4. Từ chối email trong Whitelist nhưng tài khoản bị vô hiệu hóa -> 403 ACCOUNT_DISABLED', async () => {
    const res = await request(app)
      .post('/api/auth/google')
      .send({
        email: 'disabled_user@gmail.com',
      });

    expect(res.status).toBe(403);
    expect(res.body.success).toBe(false);
    expect(res.body.code).toBe('ACCOUNT_DISABLED');
    expect(res.body.message).toContain('bị vô hiệu hóa');
  });

  it('5. Từ chối khi người dùng chọn sai vai trò (expectedRole mismatch) -> 422', async () => {
    const res = await request(app)
      .post('/api/auth/google')
      .send({
        email: 'driver01@gmail.com',
        expectedRole: 'Administrator', // driver mà chọn Admin
      });

    expect(res.status).toBe(422);
    expect(res.body.success).toBe(false);
    expect(res.body.message).toContain('không thể đăng nhập với vai trò');
  });

  it('6. Báo lỗi khi request rỗng (thiếu credential/email) -> 422', async () => {
    const res = await request(app)
      .post('/api/auth/google')
      .send({});

    expect(res.status).toBe(422);
    expect(res.body.success).toBe(false);
  });
});

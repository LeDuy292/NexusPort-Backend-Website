'use strict';

process.env.NODE_ENV = 'test';
process.env.PORT = '3003';
process.env.DB_HOST = process.env.DB_HOST || 'localhost';
process.env.DB_PORT = process.env.DB_PORT || '5432';
process.env.DB_NAME = process.env.DB_NAME || 'nexusport_test';
process.env.DB_USER = process.env.DB_USER || 'postgres';
process.env.DB_PASSWORD = process.env.DB_PASSWORD || 'postgres';
process.env.JWT_SECRET = 'test_jwt_secret_key_minimum_32_chars_ok';
process.env.JWT_EXPIRES_IN = '1h';
process.env.BCRYPT_SALT_ROUNDS = '4';

const http = require('http');
const request = require('supertest');
const bcrypt = require('bcryptjs');
const app = require('../src/app');
const { syncDB, sequelize } = require('../src/config/database');
const { initSocket, getIO } = require('../src/config/socket');
const { User } = require('../src/models/User');

describe('ĐĂNG NHẬP ĐỒNG THỜI (Single Active Session - Kickout Phiên 1, Phiên 2 thành công)', () => {
  let server;
  let io;
  let testUser;
  let token1;
  let token2;

  beforeAll(async () => {
    await syncDB({ force: true });

    server = http.createServer(app);
    io = initSocket(server);

    const hashedPassword = await bcrypt.hash('NexusPort@2026', 4);
    testUser = await User.create({
      username: 'concurrent_user',
      email: 'concurrent@nexusport.vn',
      password: hashedPassword,
      role: 'Dispatcher',
      full_name: 'Concurrent Test User',
      is_active: true,
    });
  });

  afterAll(async () => {
    if (io) io.close();
    if (server) server.close();
    await sequelize.close();
  });

  it('1. Phiên 1 đăng nhập thành công → 200 OK, cấp token1 & session_id trong DB', async () => {
    const res = await request(app).post('/api/auth/login').send({
      username: 'concurrent_user',
      password: 'NexusPort@2026',
    });

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data).toHaveProperty('token');
    token1 = res.body.data.token;

    const dbUser = await User.findByPk(testUser.id);
    expect(dbUser.session_id).toBeTruthy();
  });

  it('2. Khi Phiên 2 đăng nhập → Phiên 2 THÀNH CÔNG (200 OK), Phiên 1 BỊ KICK qua WebSocket', async () => {
    // Giả lập Socket Phiên 1 đang kết nối trong room user:userId
    const dummySocket = {
      id: 'mock_socket_session_1',
      connected: true,
      data: { user: { id: testUser.id, role: 'Dispatcher' } },
      disconnect: jest.fn(),
    };

    io.sockets.adapter.rooms.set(`user:${testUser.id}`, new Set(['mock_socket_session_1']));
    io.sockets.sockets.set('mock_socket_session_1', dummySocket);

    // Lắng nghe sự kiện auth:session_kicked được phát tới Phiên 1
    let kickedPayload = null;
    const originalEmit = io.to;
    io.to = jest.fn((room) => {
      return {
        emit: (event, payload) => {
          if (event === 'auth:session_kicked') {
            kickedPayload = payload;
          }
        },
      };
    });

    // Phiên 2 thực hiện đăng nhập
    const res2 = await request(app).post('/api/auth/login').send({
      username: 'concurrent_user',
      password: 'NexusPort@2026',
    });

    // Phiên 2 PHẢI ĐĂNG NHẬP THÀNH CÔNG (200 OK)
    expect(res2.status).toBe(200);
    expect(res2.body.success).toBe(true);
    expect(res2.body.data).toHaveProperty('token');
    token2 = res2.body.data.token;

    // Phiên 1 PHẢI BỊ KICK qua WebSocket
    expect(kickedPayload).toBeTruthy();
    expect(kickedPayload.reason).toBe('CONCURRENT_LOGIN');
    expect(kickedPayload.userId).toBe(testUser.id);

    // Database session_id được cập nhật sang sessionId của Phiên 2
    const dbUser = await User.findByPk(testUser.id);
    expect(dbUser.session_id).toBeTruthy();

    // Dọn dẹp mock socket
    io.sockets.adapter.rooms.delete(`user:${testUser.id}`);
    io.sockets.sockets.delete('mock_socket_session_1');
    io.to = originalEmit;
  });

  it('3. Phiên 1 dùng token1 cũ gọi API → Bị từ chối 401 với code SESSION_KICKED', async () => {
    const res = await request(app)
      .get('/api/auth/me')
      .set('Authorization', `Bearer ${token1}`);

    expect(res.status).toBe(401);
    expect(res.body.success).toBe(false);
    expect(res.body.code).toBe('SESSION_KICKED');
    expect(res.body.message).toContain('Phiên làm việc đã kết thúc');
  });

  it('4. Phiên 2 dùng token2 mới gọi API → 200 OK thành công', async () => {
    const res = await request(app)
      .get('/api/auth/me')
      .set('Authorization', `Bearer ${token2}`);

    expect(res.status).toBe(200);
    expect(res.body.success).toBe(true);
    expect(res.body.data.user.username).toBe('concurrent_user');
  });
});

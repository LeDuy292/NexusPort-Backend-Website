'use strict';

const http = require('http');
const { Server } = require('socket.io');
const { verifyToken } = require('../utils/jwt');

let io = null;

/**
 * Khởi tạo Socket.IO instance gắn với HTTP server.
 * @param {http.Server} server
 * @returns {Server}
 */
function initSocket(server) {
  io = new Server(server, {
    cors: {
      origin: process.env.CORS_ORIGIN || '*',
      methods: ['GET', 'POST', 'PATCH', 'PUT', 'DELETE'],
      credentials: true,
    },
    transports: ['websocket', 'polling'],
  });

  // Middleware xác thực JWT token
  io.use((socket, next) => {
    try {
      const authHeader = socket.handshake.headers?.authorization;
      const token =
        socket.handshake.auth?.token ||
        (authHeader && authHeader.startsWith('Bearer ') ? authHeader.slice(7) : null);

      if (!token) {
        return next(new Error('Yêu cầu xác thực: không tìm thấy token.'));
      }

      const decoded = verifyToken(token);
      socket.data.user = decoded;
      socket.data.driverId = decoded.id;
      socket.data.role = decoded.role;
      next();
    } catch (err) {
      next(new Error('Token xác thực không hợp lệ hoặc đã hết hạn.'));
    }
  });

  io.on('connection', (socket) => {
    const user = socket.data.user;
    const role = (user.role || '').toLowerCase();

    // Tham gia room cá nhân
    socket.join(`user:${user.id}`);

    // Tham gia room tài xế
    if (role === 'driver') {
      socket.join(`driver:${user.id}`);
    }

    // Tham gia room điều độ viên / quản trị viên
    if (role === 'dispatcher' || role === 'administrator') {
      socket.join('dispatcher:all');
    }

    socket.on('disconnect', () => {
      // Clean up tự động bởi Socket.IO
    });
  });

  return io;
}

/**
 * Lấy Socket.IO instance hiện tại.
 * @returns {Server|null}
 */
function getIO() {
  return io;
}

/**
 * Phát sự kiện tới tài xế cụ thể.
 * @param {string} driverId
 * @param {string} event
 * @param {Object} data
 */
function emitToDriver(driverId, event, data) {
  if (!io) return;
  io.to(`driver:${driverId}`).emit(event, data);
}

/**
 * Phát sự kiện tới tất cả điều độ viên và quản trị viên.
 * @param {string} event
 * @param {Object} data
 */
function emitToDispatchers(event, data) {
  if (!io) return;
  io.to('dispatcher:all').emit(event, data);
}

/**
 * Phát sự kiện khi trạng thái chuyến được cập nhật.
 * Gửi tới tài xế sở hữu chuyến và màn hình điều độ.
 * @param {Object} formattedTrip - Đối tượng chuyến đã qua formatTrip
 * @param {string} action - Tên hành động vừa thực hiện
 */
function emitTripStatusUpdated(formattedTrip, action = 'STATUS_UPDATED') {
  if (!io || !formattedTrip) return;

  const payload = {
    tripId: formattedTrip.id,
    orderCode: formattedTrip.orderCode || formattedTrip.tripCode,
    tripCode: formattedTrip.tripCode,
    status: formattedTrip.status,
    statusGroup: formattedTrip.statusGroup,
    nextStep: formattedTrip.nextStep,
    action,
    timestamp: new Date().toISOString(),
    trip: formattedTrip,
  };

  // Gửi cho tài xế sở hữu chuyến
  if (formattedTrip.driverId) {
    emitToDriver(formattedTrip.driverId, 'trip:status_updated', payload);
  }

  // Đồng thời gửi cho Điều độ viên
  emitToDispatchers('dispatcher.status.updated', {
    domain: 'driver',
    entityId: formattedTrip.id,
    status: formattedTrip.status,
    occurredAt: payload.timestamp,
    driverId: formattedTrip.driverId,
    tripCode: formattedTrip.tripCode,
    label: `Chuyến ${formattedTrip.tripCode} chuyển sang ${formattedTrip.status}`,
    metadata: { action, nextStep: formattedTrip.nextStep },
  });
}

/**
 * Phát sự kiện khi có chuyến mới được phân công cho tài xế.
 * @param {Object} formattedTrip
 */
function emitTripAssigned(formattedTrip) {
  if (!io || !formattedTrip) return;

  const payload = {
    tripId: formattedTrip.id,
    orderCode: formattedTrip.orderCode || formattedTrip.tripCode,
    tripCode: formattedTrip.tripCode,
    status: formattedTrip.status,
    statusGroup: formattedTrip.statusGroup,
    timestamp: new Date().toISOString(),
    trip: formattedTrip,
  };

  if (formattedTrip.driverId) {
    emitToDriver(formattedTrip.driverId, 'trip:assigned', payload);
  }

  emitToDispatchers('dispatcher.status.updated', {
    domain: 'driver',
    entityId: formattedTrip.id,
    status: formattedTrip.status,
    occurredAt: payload.timestamp,
    driverId: formattedTrip.driverId,
    tripCode: formattedTrip.tripCode,
    label: `Đã phân công chuyến ${formattedTrip.tripCode} cho tài xế`,
    metadata: { action: 'ASSIGNED' },
  });
}

module.exports = {
  initSocket,
  getIO,
  emitToDriver,
  emitToDispatchers,
  emitTripStatusUpdated,
  emitTripAssigned,
};

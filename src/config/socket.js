'use strict';

const http = require('http');
const { Server } = require('socket.io');
const { verifyToken } = require('../utils/jwt');
const { User } = require('../models/User');

let io = null;

/**
 * Khởi tạo Socket.IO instance gắn với HTTP server.
 * @param {http.Server} server
 * @returns {Server}
 */
function initSocket(server) {
  io = new Server(server, {
    cors: {
      origin: process.env.CORS_ORIGIN || ((origin, callback) => callback(null, true)),
      methods: ['GET', 'POST', 'PATCH', 'PUT', 'DELETE'],
      credentials: true,
    },
    transports: ['websocket', 'polling'],
  });

  // Middleware xác thực JWT token
  io.use(async (socket, next) => {
    try {
      const authHeader = socket.handshake.headers?.authorization;
      const token =
        socket.handshake.auth?.token ||
        (authHeader && authHeader.startsWith('Bearer ') ? authHeader.slice(7) : null);

      if (!token) {
        return next(new Error('Yêu cầu xác thực: không tìm thấy token.'));
      }

      const decoded = verifyToken(token);
      const account = await User.findByPk(decoded.id);
      if (!account?.is_active || account.role !== decoded.role) return next(new Error('Tài khoản hoặc vai trò không còn hợp lệ.'));
      if (decoded.sessionId && (!account.session_id || decoded.sessionId !== account.session_id)) {
        return next(new Error('Phiên làm việc đã bị kết thúc do tài khoản đăng xuất hoặc đăng nhập ở nơi khác.'));
      }
      socket.data.user = decoded;
      socket.data.driverId = decoded.id;
      socket.data.role = decoded.role;
      const expiryTimer = setTimeout(() => socket.disconnect(true), Math.max(0, decoded.exp * 1000 - Date.now()));
      expiryTimer.unref?.();
      socket.once('disconnect', () => clearTimeout(expiryTimer));
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

    // Tham gia room nhân viên bãi (Yard Operator)
    if (role === 'yard operator' || role === 'administrator') {
      socket.join('yard:all');
    }

    // Tham gia room nhân viên cổng (Gate Officer)
    if (role === 'gate officer' || role === 'administrator') {
      socket.join('gate:all');
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

/**
 * Phát sự kiện khi có yêu cầu xuất container mới gửi tới nhân viên bãi (Yard Staff).
 * @param {Object} request
 */
function emitYardOutboundRequested(request) {
  if (!io || !request) return;

  const payload = {
    requestId: request.id,
    containerId: request.container_id || request.containerId,
    truckPlate: request.truck_plate || request.truckPlate,
    driverName: request.driver_name || request.driverName,
    gateName: request.gate_name || request.gateName,
    yardSlot: request.yard_slot || request.yardSlot,
    status: request.status,
    urgency: request.urgency,
    timestamp: new Date().toISOString(),
    request,
  };

  io.to('yard:all').emit('yard:outbound_requested', payload);
  emitToDispatchers('dispatcher.status.updated', {
    domain: 'yard',
    entityId: request.id,
    status: 'PENDING',
    occurredAt: payload.timestamp,
    label: `Tài xế ${payload.driverName} (${payload.truckPlate}) đang chờ nhận cont ${payload.containerId} tại ${payload.gateName}`,
    metadata: { source: 'yard:outbound_requested' },
  });
}

/**
 * Phát sự kiện khi cẩu RTG được điều phối / bắt đầu thực hiện lệnh gắp container.
 * @param {Object} request
 */
function emitCraneDispatched(request) {
  if (!io || !request) return;

  const payload = {
    requestId: request.id,
    containerId: request.container_id || request.containerId,
    rtgCrane: request.rtg_crane || request.rtgCrane,
    rtgOperator: request.rtg_operator || request.rtgOperator,
    flowStep: request.flow_step || request.flowStep,
    status: request.status,
    yardSlot: request.yard_slot || request.yardSlot,
    timestamp: new Date().toISOString(),
  };

  io.to('yard:all').emit('yard:crane_dispatched', payload);
}

/**
 * Phát sự kiện khi Yard Staff xác nhận container đã lên xe -> Sẵn sàng xuất cổng (Ready for Gate Out).
 * Gửi tới Gate Staff và Driver.
 * @param {Object} request
 */
function emitContainerReadyForGateOut(request) {
  if (!io || !request) return;

  const payload = {
    requestId: request.id,
    containerId: request.container_id || request.containerId,
    truckPlate: request.truck_plate || request.truckPlate || request.truck,
    driverName: request.driver_name || request.driverName || request.driver,
    gateName: request.gate_name || request.gateName || request.gate,
    yardSlot: request.yard_slot || request.yardSlot || request.position,
    sealNumber: request.seal_number || request.sealNumber,
    status: 'READY_FOR_GATE_OUT',
    slotStatus: 'Available',
    timestamp: new Date().toISOString(),
    request,
  };

  // 1. Gửi tới Gate Staff (Cổng)
  io.to('gate:all').emit('gate:container_ready', payload);

  // 2. Gửi tới Yard Staff
  io.to('yard:all').emit('yard:container_ready', payload);

  // 3. Gửi tới Dispatcher
  emitToDispatchers('dispatcher.status.updated', {
    domain: 'yard',
    entityId: request.id,
    status: 'READY_FOR_GATE_OUT',
    occurredAt: payload.timestamp,
    label: `Container ${payload.containerId} đã cẩu lên xe ${payload.truckPlate}. Sẵn sàng cho xuất cổng ${payload.gateName}!`,
    metadata: { slotReleased: payload.yardSlot },
  });

  // 4. Gửi tới Driver nếu có driverId
  const driverId = request.driver_id || request.driverId;
  if (driverId) {
    emitToDriver(driverId, 'trip:status_updated', {
      tripId: request.trip_id || request.tripId,
      status: 'Container Picked Up',
      containerId: payload.containerId,
      message: 'Container đã được cẩu lên xe an toàn. Bạn có thể tiến ra cổng để xuất bãi.',
      timestamp: payload.timestamp,
    });
  }
}

/**
 * Phát sự kiện kick session cũ khi tài khoản đăng nhập trên thiết bị / phiên khác.
 * @param {string} userId
 * @param {string} reason
 * @param {string} message
 */
function kickUserSockets(userId, reason = 'CONCURRENT_LOGIN', message = 'Tài khoản của bạn vừa được đăng nhập trên một thiết bị/trình duyệt khác. Phiên làm việc này đã bị đăng xuất.') {
  if (!io || !userId) return;
  const payload = {
    userId,
    reason,
    message,
    timestamp: new Date().toISOString(),
  };

  // Gửi sự kiện cho toàn bộ client socket thuộc user này
  io.to(`user:${userId}`).emit('auth:session_kicked', payload);

  // Ngắt kết nối socket của các phiên cũ
  const userRoom = io.sockets?.adapter?.rooms?.get(`user:${userId}`);
  if (userRoom) {
    const socketIds = Array.from(userRoom);
    for (const sid of socketIds) {
      const s = io.sockets.sockets.get(sid);
      if (s) {
        s.disconnect(true);
      }
    }
  }
}

module.exports = {
  initSocket,
  getIO,
  emitToDriver,
  emitToDispatchers,
  emitTripStatusUpdated,
  emitTripAssigned,
  emitYardOutboundRequested,
  emitCraneDispatched,
  emitContainerReadyForGateOut,
  kickUserSockets,
};

'use strict';

/**
 * Protected Routes — Demo RBAC cho NexusPort
 *
 * Mỗi endpoint đại diện cho một chức năng nghiệp vụ thực tế.
 * Được bảo vệ bởi chuỗi: authenticate → authorize([roles]) → handler.
 *
 * Mục đích:
 *  - Kiểm thử RBAC integration test
 *  - Làm mẫu để các module nghiệp vụ thực tế thêm vào
 */

const { Router } = require('express');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const { PERMISSIONS } = require('../../config/rbac');

const router = Router();

// ─── Helper handler nhỏ, trả về thông tin endpoint + user ────────────────────
const ok = (name) => (req, res) => {
  res.json({
    success: true,
    endpoint: name,
    user: {
      id:       req.user.id,
      username: req.user.username,
      role:     req.user.role,
    },
  });
};

// ─── Administrator ───────────────────────────────────────────────────────────

/**
 * @swagger
 * /api/admin/users:
 *   get:
 *     summary: Danh sách tất cả người dùng (Administrator only)
 *     tags: [RBAC Demo]
 *     security:
 *       - BearerAuth: []
 *     responses:
 *       200: { description: OK }
 *       401: { description: Chưa xác thực }
 *       403: { description: Không đủ quyền }
 */
router.get(
  '/admin/users',
  authenticate,
  authorize(PERMISSIONS.ADMIN_ONLY),
  ok('GET /api/admin/users — Administrator only')
);

/**
 * @swagger
 * /api/admin/dashboard:
 *   get:
 *     summary: Dashboard vận hành (Administrator + Dispatcher)
 *     tags: [RBAC Demo]
 *     security:
 *       - BearerAuth: []
 */
router.get(
  '/admin/dashboard',
  authenticate,
  authorize(PERMISSIONS.OPERATIONS),
  ok('GET /api/admin/dashboard — Administrator + Dispatcher')
);

// ─── Dispatcher ──────────────────────────────────────────────────────────────

/**
 * @swagger
 * /api/dispatcher/operations:
 *   get:
 *     summary: Điều phối vận hành cảng (Administrator + Dispatcher)
 *     tags: [RBAC Demo]
 *     security:
 *       - BearerAuth: []
 */
router.get(
  '/dispatcher/operations',
  authenticate,
  authorize(PERMISSIONS.OPERATIONS),
  ok('GET /api/dispatcher/operations — Dispatcher + Administrator')
);

// ─── Gate Officer ────────────────────────────────────────────────────────────

/**
 * @swagger
 * /api/gate/check-in:
 *   get:
 *     summary: Kiểm tra xe vào cổng (Administrator + Gate Officer)
 *     tags: [RBAC Demo]
 *     security:
 *       - BearerAuth: []
 */
router.get(
  '/gate/check-in',
  authenticate,
  authorize(PERMISSIONS.GATE_ACCESS),
  ok('GET /api/gate/check-in — Gate Officer + Administrator')
);

// ─── Yard Operator ────────────────────────────────────────────────────────────

/**
 * @swagger
 * /api/yard/containers:
 *   get:
 *     summary: Quản lý bãi container (Administrator + Yard Operator)
 *     tags: [RBAC Demo]
 *     security:
 *       - BearerAuth: []
 */
router.get(
  '/yard/containers',
  authenticate,
  authorize(PERMISSIONS.YARD_ACCESS),
  ok('GET /api/yard/containers — Yard Operator + Administrator')
);

// ─── Berth Staff ──────────────────────────────────────────────────────────────

/**
 * @swagger
 * /api/berth/schedule:
 *   get:
 *     summary: Lịch cầu tàu (Administrator + Berth Staff)
 *     tags: [RBAC Demo]
 *     security:
 *       - BearerAuth: []
 */
router.get(
  '/berth/schedule',
  authenticate,
  authorize(PERMISSIONS.BERTH_ACCESS),
  ok('GET /api/berth/schedule — Berth Staff + Administrator')
);

// ─── Transport Company ────────────────────────────────────────────────────────

/**
 * @swagger
 * /api/transport/bookings:
 *   get:
 *     summary: Quản lý booking (Administrator + Transport Company + Dispatcher)
 *     tags: [RBAC Demo]
 *     security:
 *       - BearerAuth: []
 */
router.get(
  '/transport/bookings',
  authenticate,
  authorize(PERMISSIONS.TRANSPORT_ACCESS),
  ok('GET /api/transport/bookings — Transport Company + Dispatcher + Administrator')
);

// ─── Driver ───────────────────────────────────────────────────────────────────

/**
 * @swagger
 * /api/driver/trips:
 *   get:
 *     summary: Chuyến của tài xế (Administrator + Driver)
 *     tags: [RBAC Demo]
 *     security:
 *       - BearerAuth: []
 */
router.get(
  '/driver/trips',
  authenticate,
  authorize(PERMISSIONS.DRIVER_ACCESS),
  ok('GET /api/driver/trips — Driver + Administrator')
);

// ─── Authenticated only (mọi role) ───────────────────────────────────────────

/**
 * @swagger
 * /api/profile/me:
 *   get:
 *     summary: Thông tin cá nhân (mọi role đã đăng nhập)
 *     tags: [RBAC Demo]
 *     security:
 *       - BearerAuth: []
 */
router.get(
  '/profile/me',
  authenticate,
  authorize(PERMISSIONS.AUTHENTICATED),
  ok('GET /api/profile/me — All authenticated roles')
);

module.exports = router;

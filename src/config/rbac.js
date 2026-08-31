'use strict';

/**
 * ─── ROLE CONSTANTS ──────────────────────────────────────────────────────────
 * Danh sách vai trò hợp lệ trong hệ thống NexusPort.
 * Phải khớp với VALID_ROLES trong models/User.js
 */
const ROLES = Object.freeze({
  ADMINISTRATOR:     'Administrator',
  TRANSPORT_COMPANY: 'Transport Company',
  DRIVER:            'Driver',
  DISPATCHER:        'Dispatcher',
  GATE_OFFICER:      'Gate Officer',
  YARD_OPERATOR:     'Yard Operator',
  BERTH_STAFF:       'Berth Staff',
});

/**
 * ─── PERMISSION GROUPS ───────────────────────────────────────────────────────
 * Nhóm quyền cho từng chức năng nghiệp vụ.
 * - Sử dụng trong authorize middleware để khai báo ngắn gọn hơn.
 * - Administrator luôn có mặt trong mọi nhóm (quyền tối cao).
 */
const PERMISSIONS = Object.freeze({
  // Quản trị hệ thống — chỉ Administrator
  ADMIN_ONLY: [ROLES.ADMINISTRATOR],

  // Điều phối vận hành tổng thể
  OPERATIONS: [
    ROLES.ADMINISTRATOR,
    ROLES.DISPATCHER,
  ],

  // Kiểm soát cổng vào ra
  GATE_ACCESS: [
    ROLES.ADMINISTRATOR,
    ROLES.GATE_OFFICER,
  ],

  // Vận hành bãi container
  YARD_ACCESS: [
    ROLES.ADMINISTRATOR,
    ROLES.YARD_OPERATOR,
  ],

  // Vận hành cầu tàu
  BERTH_ACCESS: [
    ROLES.ADMINISTRATOR,
    ROLES.BERTH_STAFF,
  ],

  // Hãng tàu / đặt booking
  TRANSPORT_ACCESS: [
    ROLES.ADMINISTRATOR,
    ROLES.TRANSPORT_COMPANY,
    ROLES.DISPATCHER,
  ],

  // Tài xế — xem chuyến / check-in
  DRIVER_ACCESS: [
    ROLES.ADMINISTRATOR,
    ROLES.DRIVER,
  ],

  // Mọi user đã đăng nhập (tất cả roles)
  AUTHENTICATED: Object.values(ROLES),
});

module.exports = { ROLES, PERMISSIONS };

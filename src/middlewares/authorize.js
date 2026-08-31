'use strict';

/**
 * ─── AUTHORIZE MIDDLEWARE ────────────────────────────────────────────────────
 *
 * Factory function trả về một Express middleware kiểm tra role của user.
 *
 * Cách sử dụng:
 *   const { authorize } = require('../middlewares/authorize');
 *   const { PERMISSIONS } = require('../config/rbac');
 *
 *   router.get('/admin/users', authenticate, authorize(PERMISSIONS.ADMIN_ONLY), handler);
 *   router.get('/gate/check-in', authenticate, authorize('Gate Officer', 'Administrator'), handler);
 *
 * @param {...string} allowedRoles - Danh sách roles được phép truy cập endpoint này.
 * @returns {Function} Express middleware
 */
function authorize(...allowedRoles) {
  // Flatten nếu người dùng truyền vào mảng (vd: authorize(PERMISSIONS.GATE_ACCESS))
  const roles = allowedRoles.flat();

  return function authorizationMiddleware(req, res, next) {
    // authenticate phải chạy trước → req.user phải tồn tại
    if (!req.user) {
      return res.status(401).json({
        success: false,
        message: 'Yêu cầu xác thực. Vui lòng đăng nhập.',
      });
    }

    const userRole = req.user.role;

    if (!roles.includes(userRole)) {
      return res.status(403).json({
        success: false,
        message: `Bạn không có quyền thực hiện hành động này.`,
        detail: {
          yourRole: userRole,
          requiredRoles: roles,
        },
      });
    }

    next();
  };
}

/**
 * Shorthand: chỉ cho Administrator
 */
function requireAdmin(req, res, next) {
  return authorize('Administrator')(req, res, next);
}

/**
 * Shorthand: tất cả roles đã đăng nhập
 * Chỉ cần authenticate — không cần role cụ thể.
 * Giữ để nhất quán với cú pháp middleware chain.
 */
function requireAuthenticated(req, res, next) {
  if (!req.user) {
    return res.status(401).json({
      success: false,
      message: 'Yêu cầu xác thực. Vui lòng đăng nhập.',
    });
  }
  next();
}

module.exports = { authorize, requireAdmin, requireAuthenticated };

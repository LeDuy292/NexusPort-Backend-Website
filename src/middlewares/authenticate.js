'use strict';

const { verifyToken } = require('../utils/jwt');
const { User } = require('../models/User');
const { recordUserHeartbeat } = require('../modules/auth/auth.service');

/**
 * Middleware xác thực JWT.
 * Yêu cầu header: Authorization: Bearer <token>
 *
 * Nếu hợp lệ: gắn req.user = decoded payload và gọi next().
 * Nếu không:  trả 401 Unauthorized.
 */
async function authenticate(req, res, next) {
  try {
    const authHeader = req.headers.authorization;

    if (!authHeader || !authHeader.startsWith('Bearer ')) {
      return res.status(401).json({
        success: false,
        message: 'Yêu cầu xác thực. Vui lòng đăng nhập.',
      });
    }

    const token = authHeader.split(' ')[1];

    let decoded;
    try {
      decoded = verifyToken(token);
    } catch (err) {
      const message =
        err.name === 'TokenExpiredError'
          ? 'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.'
          : 'Token không hợp lệ.';
      return res.status(401).json({
        success: false,
        code: err.name === 'TokenExpiredError' ? 'TOKEN_EXPIRED' : 'TOKEN_INVALID',
        message,
      });
    }

    // Kiểm tra user vẫn còn tồn tại và đang active
    const user = await User.findByPk(decoded.id);
    if (!user || !user.is_active || (user.role === 'Driver' && (!user.email_verified_at || user.invitation_pending))) {
      return res.status(401).json({
        success: false,
        message: 'Tài khoản không tồn tại hoặc đã bị vô hiệu hóa.',
      });
    }

    // Kiểm tra phiên hoạt động duy nhất (Single Active Session)
// 1) Nếu tài khoản có session_id trong DB nhưng token không trùng khớp -> bị kick bởi thiết bị khác!
    if (user.session_id && (!decoded.sessionId || decoded.sessionId !== user.session_id)) {
      return res.status(401).json({
        success: false,
        code: 'SESSION_KICKED',
        message: 'Tài khoản của bạn vừa đăng nhập trên thiết bị khác. Vui lòng đăng nhập lại.',
      });
    }

    // 2) Nếu tài khoản không còn session_id trong DB mà token mang sessionId -> phiên đã đăng xuất bình thường!
    if (!user.session_id && decoded.sessionId) {
      return res.status(401).json({
        success: false,
        code: 'SESSION_LOGGED_OUT',
        message: 'Phiên đăng nhập đã kết thúc. Vui lòng đăng nhập lại.',
      });
    }

    // Cập nhật heartbeat hoạt động thời gian thực (nếu service khả dụng)
    if (typeof recordUserHeartbeat === 'function') {
      recordUserHeartbeat(user.id);
    }

    req.user = decoded;
    // Reuse this fresh DB read within this request; never cache across requests.
    req.authenticatedAccount = user;
    next();
  } catch (error) {
    next(error);
  }
}

module.exports = { authenticate };

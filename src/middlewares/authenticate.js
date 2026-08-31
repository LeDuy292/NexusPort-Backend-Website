'use strict';

const { verifyToken } = require('../utils/jwt');
const { User } = require('../models/User');

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
      return res.status(401).json({ success: false, message });
    }

    // Kiểm tra user vẫn còn tồn tại và đang active
    const user = await User.findByPk(decoded.id);
    if (!user || !user.is_active) {
      return res.status(401).json({
        success: false,
        message: 'Tài khoản không tồn tại hoặc đã bị vô hiệu hóa.',
      });
    }

    req.user = decoded;
    next();
  } catch (error) {
    next(error);
  }
}

module.exports = { authenticate };

'use strict';

const { validationResult } = require('express-validator');
const authService = require('./auth.service');
const { User } = require('../../models/User');

/**
 * POST /api/auth/login
 * Đăng nhập bằng username/email + password.
 */
async function loginController(req, res, next) {
  try {
    // Kiểm tra lỗi validation từ express-validator
    const errors = validationResult(req);
    if (!errors.isEmpty()) {
      return res.status(422).json({
        success: false,
        message: errors.array()[0].msg,
        errors: errors.array().map((e) => ({ field: e.path, message: e.msg })),
      });
    }

    const { username, password, rememberMe, expectedRole } = req.body;

    const { token, user } = await authService.login(username, password, !!rememberMe, expectedRole);

    return res.status(200).json({
      success: true,
      message: 'Đăng nhập thành công.',
      data: {
        token,
        user,
      },
    });
  } catch (error) {
    // Lỗi có statusCode (401, 403, 422) từ service → trả trực tiếp
    if (error.statusCode) {
      return res.status(error.statusCode).json({
        success: false,
        message: error.message,
      });
    }
    next(error);
  }
}

/**
 * POST /api/auth/forgot-password
 * Khởi tạo yêu cầu quên mật khẩu (sinh mã OTP 6 số).
 */
async function forgotPasswordController(req, res, next) {
  try {
    const errors = validationResult(req);
    if (!errors.isEmpty()) {
      return res.status(422).json({
        success: false,
        message: errors.array()[0].msg,
      });
    }

    const { email } = req.body;
    const result = await authService.forgotPassword(email);

    return res.status(200).json({
      success: true,
      message: result.message,
      data: {
        otp: result.otp, // Trả về OTP trực tiếp cho mục đích demo / test dễ dàng
        email: result.email,
        username: result.username,
      },
    });
  } catch (error) {
    if (error.statusCode) {
      return res.status(error.statusCode).json({
        success: false,
        message: error.message,
      });
    }
    next(error);
  }
}

/**
 * POST /api/auth/reset-password
 * Đặt lại mật khẩu bằng mã OTP.
 */
async function resetPasswordController(req, res, next) {
  try {
    const errors = validationResult(req);
    if (!errors.isEmpty()) {
      return res.status(422).json({
        success: false,
        message: errors.array()[0].msg,
      });
    }

    const { email, otp, newPassword } = req.body;
    const result = await authService.resetPassword(email, otp, newPassword);

    return res.status(200).json({
      success: true,
      message: result.message,
    });
  } catch (error) {
    if (error.statusCode) {
      return res.status(error.statusCode).json({
        success: false,
        message: error.message,
      });
    }
    next(error);
  }
}

/**
 * GET /api/auth/me
 * Lấy thông tin user hiện tại từ JWT đã xác thực.
 * Middleware `authenticate` phải chạy trước.
 */
async function getMeController(req, res, next) {
  try {
    // req.user được gắn bởi authenticate middleware
    const user = await User.findByPk(req.user.id);
    if (!user) {
      return res.status(404).json({
        success: false,
        message: 'Không tìm thấy người dùng.',
      });
    }

    return res.status(200).json({
      success: true,
      data: {
        user: user.toSafeObject(),
      },
    });
  } catch (error) {
    next(error);
  }
}

/**
 * POST /api/auth/logout
 * Đăng xuất người dùng: xóa session_id trong DB, ngắt socket, xóa cookie (nếu có).
 */
async function logoutController(req, res) {
  try {
    const authHeader = req.headers.authorization;
    if (authHeader && authHeader.startsWith('Bearer ')) {
      const token = authHeader.split(' ')[1];
      try {
        const { verifyToken } = require('../../utils/jwt');
        const decoded = verifyToken(token);
        if (decoded && decoded.id) {
          await User.update({ session_id: null }, { where: { id: decoded.id } });
          const { kickUserSockets } = require('../../config/socket');
          kickUserSockets(decoded.id, 'LOGGED_OUT', 'Bạn đã đăng xuất.');
        }
      } catch (_) {}
    }
    if (res.clearCookie) {
      res.clearCookie('token');
      res.clearCookie('refreshToken');
    }
  } catch (_) {}
  return res.status(200).json({
    success: true,
    message: 'Đăng xuất thành công.',
  });
}

module.exports = {
  loginController,
  logoutController,
  getMeController,
  forgotPasswordController,
  resetPasswordController,
};


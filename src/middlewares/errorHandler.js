'use strict';

const { nodeEnv } = require('../config/env');

/**
 * Global error handler middleware.
 * Phải được đăng ký CUỐI CÙNG sau tất cả routes.
 */
// eslint-disable-next-line no-unused-vars
function errorHandler(err, req, res, next) {
  // Log lỗi đầy đủ ở môi trường dev
  if (nodeEnv === 'development') {
    console.error('[ErrorHandler]', err);
  } else {
    console.error('[ErrorHandler]', err.message);
  }

  // Sequelize validation errors
  if (err.name === 'SequelizeValidationError' || err.name === 'SequelizeUniqueConstraintError') {
    const messages = err.errors.map((e) => e.message);
    return res.status(422).json({
      success: false,
      message: 'Dữ liệu không hợp lệ.',
      errors: messages,
    });
  }

  // JWT errors (đã xử lý ở middleware authenticate, nhưng để an toàn)
  if (err.name === 'JsonWebTokenError' || err.name === 'TokenExpiredError') {
    return res.status(401).json({
      success: false,
      message: 'Token không hợp lệ hoặc đã hết hạn.',
    });
  }

  // Default: 500 Internal Server Error
  const statusCode = err.statusCode || err.status || 500;
  const message =
    nodeEnv === 'production'
      ? 'Lỗi hệ thống nội bộ. Vui lòng thử lại sau.'
      : err.message || 'Lỗi không xác định.';

  return res.status(statusCode).json({
    success: false,
    message,
    ...(nodeEnv === 'development' && { stack: err.stack }),
  });
}

module.exports = { errorHandler };

'use strict';

const jwt = require('jsonwebtoken');
const { jwt: jwtConfig } = require('../config/env');

/**
 * Tạo JWT token từ payload user.
 * @param {Object} payload - { id, username, email, role }
 * @param {string} [expiresIn] - thời gian hết hạn tùy chọn (mặc định lấy từ config, e.g. '30d' hoặc '8h')
 * @returns {string} JWT token
 */
function generateToken(payload, expiresIn) {
  return jwt.sign(payload, jwtConfig.secret, {
    expiresIn: expiresIn || jwtConfig.expiresIn,
    algorithm: 'HS256',
  });
}

/**
 * Xác thực và decode JWT token.
 * @param {string} token
 * @returns {Object} decoded payload
 * @throws {Error} nếu token không hợp lệ hoặc đã hết hạn
 */
function verifyToken(token) {
  return jwt.verify(token, jwtConfig.secret, { algorithms: ['HS256'] });
}

module.exports = { generateToken, verifyToken };

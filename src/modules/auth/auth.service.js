'use strict';

const bcrypt = require('bcryptjs');
const { Op } = require('sequelize');
const { User } = require('../../models/User');
const { generateToken } = require('../../utils/jwt');

const { bcrypt: bcryptConfig } = require('../../config/env');

/**
 * Xử lý đăng nhập:
 * 1. Tìm user theo username hoặc email (case-insensitive)
 * 2. Kiểm tra account status
 * 3. So sánh password với bcrypt
 * 4. Tạo và trả về JWT (nếu rememberMe = true -> thời hạn 30 ngày, ngược lại 8 giờ)
 *
 * @param {string} usernameOrEmail
 * @param {string} password
 * @param {boolean} [rememberMe=false]
 * @returns {{ token: string, user: Object }}
 * @throws {{ statusCode: number, message: string }}
 */
async function login(usernameOrEmail, password, rememberMe = false) {
  // Tìm user theo username hoặc email (không phân biệt chữ hoa/thường)
  const user = await User.findOne({
    where: {
      [Op.or]: [
        { username: { [Op.iLike]: usernameOrEmail } },
        { email: { [Op.iLike]: usernameOrEmail } },
      ],
    },
  });

  if (!user) {
    // Vẫn chạy bcrypt để tránh timing attack
    await bcrypt.compare(password, '$2b$12$invalidhashplaceholderXXXXXXXXXXXXXX');
    const err = new Error('Tài khoản không tồn tại!');
    err.statusCode = 401;
    throw err;
  }

  // Kiểm tra tài khoản có đang active không
  if (!user.is_active) {
    const err = new Error('Tài khoản đã bị vô hiệu hóa. Vui lòng liên hệ quản trị viên.');
    err.statusCode = 403;
    throw err;
  }

  // So sánh password
  const isPasswordValid = await bcrypt.compare(password, user.password);
  if (!isPasswordValid) {
    const err = new Error('Mật khẩu không chính xác!');
    err.statusCode = 401;
    throw err;
  }

  // Fetch CarrierId if user is Transport Company
  let carrierId = null;
  if (user.role === 'Transport Company' || user.role === 'Carrier') {
    const { sequelize } = require('../../config/database');
    const [results] = await sequelize.query('SELECT carrier_id FROM carrier_users WHERE user_id = :userId', {
      replacements: { userId: user.id }
    });
    if (results && results.length > 0) {
      carrierId = results[0].carrier_id;
    }
  }

  // Tạo JWT payload
  const payload = {
    id: user.id,
    sub: user.id, // maps to ClaimTypes.NameIdentifier
    "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier": user.id, // Explicit NameIdentifier
    username: user.username,
    name: user.username, // maps to ClaimTypes.Name
    email: user.email,
    role: user.role,
    "http://schemas.microsoft.com/ws/2008/06/identity/claims/role": user.role, // explicitly provide ClaimTypes.Role
    ...(carrierId && { CarrierId: carrierId }),
  };

  // Nếu người dùng tích "Ghi nhớ đăng nhập" (rememberMe) -> Token hết hạn sau 30 ngày ('30d'), ngược lại '8h'
  const tokenExpiry = rememberMe ? '30d' : '8h';
  const token = generateToken(payload, tokenExpiry);

  return {
    token,
    user: user.toSafeObject(),
  };
}

/**
 * Yêu cầu quên mật khẩu:
 * Tìm tài khoản theo email hoặc username, sinh mã OTP 6 số ngẫu nhiên, lưu vào DB có hạn 15 phút.
 *
 * @param {string} emailOrUsername
 * @returns {Promise<{ message: string, otp: string, email: string }>}
 */
async function forgotPassword(emailOrUsername) {
  const user = await User.findOne({
    where: {
      [Op.or]: [
        { username: { [Op.iLike]: emailOrUsername } },
        { email: { [Op.iLike]: emailOrUsername } },
      ],
    },
  });

  if (!user) {
    const err = new Error('Email hoặc tên đăng nhập không tồn tại trong hệ thống!');
    err.statusCode = 404;
    throw err;
  }

  if (!user.is_active) {
    const err = new Error('Tài khoản đã bị vô hiệu hóa.');
    err.statusCode = 403;
    throw err;
  }

  // Sinh mã OTP ngẫu nhiên 6 chữ số
  const otpCode = Math.floor(100000 + Math.random() * 900000).toString();
  const tokenExp = new Date(Date.now() + 15 * 60 * 1000); // 15 phút

  user.reset_token = otpCode;
  user.reset_token_exp = tokenExp;
  await user.save();

  return {
    message: 'Mã xác thực khôi phục mật khẩu đã được khởi tạo.',
    otp: otpCode,
    email: user.email,
    username: user.username,
  };
}

/**
 * Đặt lại mật khẩu bằng mã OTP:
 * Xác thực mã OTP và cập nhật mật khẩu mới đã hash vào database.
 *
 * @param {string} emailOrUsername
 * @param {string} otp
 * @param {string} newPassword
 * @returns {Promise<{ message: string }>}
 */
async function resetPassword(emailOrUsername, otp, newPassword) {
  const user = await User.findOne({
    where: {
      [Op.or]: [
        { username: { [Op.iLike]: emailOrUsername } },
        { email: { [Op.iLike]: emailOrUsername } },
      ],
    },
  });

  if (!user) {
    const err = new Error('Không tìm thấy tài khoản hợp lệ!');
    err.statusCode = 404;
    throw err;
  }

  if (!user.reset_token || user.reset_token !== otp) {
    const err = new Error('Mã OTP xác thực không đúng!');
    err.statusCode = 400;
    throw err;
  }

  if (!user.reset_token_exp || new Date(user.reset_token_exp) < new Date()) {
    const err = new Error('Mã OTP xác thực đã hết hạn! Vui lòng yêu cầu mã mới.');
    err.statusCode = 400;
    throw err;
  }

  // Hash mật khẩu mới
  const hashedPassword = await bcrypt.hash(newPassword, bcryptConfig.saltRounds);

  user.password = hashedPassword;
  user.reset_token = null;
  user.reset_token_exp = null;
  await user.save();

  return {
    message: 'Đặt lại mật khẩu thành công! Bạn có thể đăng nhập bằng mật khẩu mới.',
  };
}

module.exports = { login, forgotPassword, resetPassword };

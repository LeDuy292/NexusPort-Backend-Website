'use strict';

const crypto = require('crypto');
const bcrypt = require('bcryptjs');
const { Op } = require('sequelize');
const { User } = require('../../models/User');
const { generateToken } = require('../../utils/jwt');
const { kickUserSockets } = require('../../config/socket');

const { bcrypt: bcryptConfig } = require('../../config/env');

/**
 * Xử lý đăng nhập:
 * 1. Tìm user theo username hoặc email (case-insensitive)
 * 2. Kiểm tra account status
 * 3. So sánh password với bcrypt
 * 4. Kiểm tra vai trò mong muốn (expectedRole) nếu được cung cấp
 * 5. Kick các phiên đăng nhập cũ (Single Active Session)
 * 6. Tạo sessionId mới, lưu vào users.session_id và JWT payload
 * 7. Tạo và trả về JWT (nếu rememberMe = true -> thời hạn 30 ngày, ngược lại 8 giờ)
 *
 * @param {string} usernameOrEmail
 * @param {string} password
 * @param {boolean} [rememberMe=false]
 * @param {string} [expectedRole=null]
 * @returns {{ token: string, user: Object }}
 * @throws {{ statusCode: number, message: string }}
 */
async function login(usernameOrEmail, password, rememberMe = false, expectedRole = null) {
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
    const err = new Error('Tài khoản hoặc mật khẩu không chính xác.');
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
    const err = new Error('Tài khoản hoặc mật khẩu không chính xác.');
    err.statusCode = 401;
    throw err;
  }

  // Kiểm tra vai trò đã chọn (nếu client gửi expectedRole)
  if (expectedRole) {
    const normExpected = expectedRole.trim().toLowerCase();
    const normActual = (user.role || '').trim().toLowerCase();
    const isRoleMatch =
      normExpected === normActual ||
      (['carrier', 'transport company', 'carrier staff'].includes(normExpected) &&
        ['carrier', 'transport company', 'carrier staff'].includes(normActual)) ||
      (['dispatcher', 'operator'].includes(normExpected) &&
        ['dispatcher', 'operator'].includes(normActual)) ||
      (['yard', 'yard operator', 'yard staff'].includes(normExpected) &&
        ['yard', 'yard operator', 'yard staff'].includes(normActual)) ||
      (['gate', 'gate officer'].includes(normExpected) &&
        ['gate', 'gate officer'].includes(normActual)) ||
      (['berth', 'berth staff'].includes(normExpected) &&
        ['berth', 'berth staff'].includes(normActual));

    if (!isRoleMatch) {
      const err = new Error(
        `Tài khoản "${user.username}" có vai trò là "${user.role}", không thể đăng nhập với vai trò "${expectedRole}". Vui lòng chọn đúng vai trò.`
      );
      err.statusCode = 422;
      throw err;
    }
  }

  // KICK PHIÊN CŨ (Concurrent login kick-out):
  // Nếu user đã có session_id hoặc đang có kết nối socket, lập tức phát lệnh kick toàn bộ phiên cũ
  try {
    kickUserSockets(
      user.id,
      'CONCURRENT_LOGIN',
      'Tài khoản của bạn vừa được đăng nhập trên một thiết bị/trình duyệt khác. Phiên làm việc này đã bị đăng xuất.'
    );
  } catch (socketErr) {
    console.error('[AuthService] Lỗi khi kickUserSockets:', socketErr.message);
  }

  // Tạo sessionId mới duy nhất cho phiên đăng nhập hiện tại
  const sessionId = crypto.randomUUID();
  user.session_id = sessionId;
  await user.save();

  // Fetch CarrierId if user is Transport Company, Carrier, or Carrier Staff
  let carrierId = null;
  if (user.role === 'Transport Company' || user.role === 'Carrier' || user.role === 'Carrier Staff') {
    const { sequelize } = require('../../config/database');
    const [results] = await sequelize.query('SELECT carrier_id FROM carrier_users WHERE user_id = :userId', {
      replacements: { userId: user.id },
    });
    if (results && results.length > 0) {
      carrierId = results[0].carrier_id;
    }
  }

  // Tạo JWT payload có gắn sessionId
  const payload = {
    id: user.id,
    sub: user.id, // maps to ClaimTypes.NameIdentifier
    "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier": user.id, // Explicit NameIdentifier
    username: user.username,
    name: user.username, // maps to ClaimTypes.Name
    email: user.email,
    role: user.role,
    sessionId: sessionId,
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

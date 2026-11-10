'use strict';

const crypto = require('crypto');
const bcrypt = require('bcryptjs');
const { Op } = require('sequelize');
const { sequelize } = require('../../config/database');
const { User } = require('../../models/User');
const { generateToken } = require('../../utils/jwt');
const { kickUserSockets, hasActiveSockets } = require('../../config/socket');

// Map lưu thời điểm hoạt động gần nhất (heartbeat) của user: userId -> timestamp (ms)
const activeHeartbeats = new Map();

function recordUserHeartbeat(userId) {
  if (userId) {
    activeHeartbeats.set(userId, Date.now());
  }
}

function clearUserHeartbeat(userId) {
  if (userId) {
    activeHeartbeats.delete(userId);
  }
}

function isUserCurrentlyActive(userId) {
  if (!userId) return false;
  // Kiểm tra người dùng có kết nối socket đang hoạt động (trên web / mobile)
  return hasActiveSockets(userId);
}

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
  let user = await User.findOne({
    where: {
      [Op.or]: [
        { username: { [Op.iLike]: usernameOrEmail } },
        { email: { [Op.iLike]: usernameOrEmail } },
      ],
    },
  });

  // Hỗ trợ tài xế đăng nhập bằng Số điện thoại hoặc Số bằng lái xe (GPLX)
  if (!user && usernameOrEmail) {
    try {
      const rawInput = usernameOrEmail.trim();
      const [linkedDriver] = await sequelize.query(`
        SELECT d.user_id
        FROM drivers d
        WHERE (d.phone = :rawInput OR d.license_number ILIKE :rawInput)
          AND d.user_id IS NOT NULL
          AND d.status::text NOT IN ('inactive', 'banned')
        LIMIT 1
      `, {
        replacements: { rawInput },
        type: sequelize.QueryTypes.SELECT,
      });

      if (linkedDriver && linkedDriver.user_id) {
        user = await User.findByPk(linkedDriver.user_id);
      }
    } catch (_e) {
      // ignore
    }
  }

  // Tự động kích hoạt tài khoản nếu tài xế đã có hồ sơ trong bảng drivers nhưng chưa có trong users
  if (!user && password && password.length >= 6 && expectedRole && expectedRole.toLowerCase() === 'driver') {
    try {
      const rawInput = (usernameOrEmail || '').trim();
      const [orphanedDriver] = await sequelize.query(`
        SELECT d.id, d.carrier_id, d.full_name, d.phone, d.license_number
        FROM drivers d
        WHERE d.user_id IS NULL AND d.status::text NOT IN ('inactive', 'banned')
          AND (d.phone = :rawInput OR d.license_number ILIKE :rawInput)
        LIMIT 1
      `, {
        replacements: { rawInput },
        type: sequelize.QueryTypes.SELECT,
      });

      if (orphanedDriver) {
        const usersService = require('../users/users.service');
        const loginPrefix = orphanedDriver.phone || orphanedDriver.license_number.replace(/[^a-zA-Z0-9]/g, '').toLowerCase();
        const provRes = await usersService.createDriverAccount({
          username: loginPrefix,
          email: `${loginPrefix}@nexusport.vn`,
          password,
          fullName: orphanedDriver.full_name,
          driverId: orphanedDriver.id,
          carrierId: orphanedDriver.carrier_id,
        });
        user = await User.findByPk(provRes.user.id);
        console.log(`[AuthService] Tự động khởi tạo và liên kết tài khoản User cho tài xế ${orphanedDriver.full_name} (${orphanedDriver.id})`);
      }
    } catch (provErr) {
      console.warn('[AuthService] Tự động cấp tài khoản cho tài xế lỗi:', provErr.message);
    }
  }

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
      (['berth', 'berth staff', 'shipping line', 'shippingline', 'hãng tàu'].includes(normExpected) &&
        ['berth', 'berth staff', 'shipping line', 'shippingline', 'hãng tàu'].includes(normActual));

    if (!isRoleMatch) {
      const err = new Error(
        `Tài khoản "${user.username}" có vai trò là "${user.role}", không thể đăng nhập với vai trò "${expectedRole}". Vui lòng chọn đúng vai trò.`
      );
      err.statusCode = 422;
      throw err;
    }
  }

  // 5. XỬ LÝ PHIÊN ĐĂNG NHẬP (Single Active Session - Kickout Phiên 1, Phiên 2 thành công):
  // Nếu tài khoản đang có phiên hoạt động (có kết nối socket hoặc session_id trước đó),
  // ngay lập tức kick phiên cũ (Phiên 1) ra khỏi hệ thống qua WebSocket.
  if (isUserCurrentlyActive(user.id) || user.session_id) {
    console.warn(`[AuthService] Tài khoản "${user.username}" (${user.id}) có phiên mới đăng nhập. Cưỡng chế kick phiên cũ ra khỏi hệ thống.`);
    try {
      kickUserSockets(
        user.id,
        'CONCURRENT_LOGIN',
        'Tài khoản của bạn vừa được đăng nhập trên một thiết bị hoặc trình duyệt khác. Để bảo vệ an toàn tài khoản, phiên làm việc trước đó đã tự động đăng xuất.'
      );
    } catch (socketErr) {
      console.error('[AuthService] Lỗi khi kickUserSockets:', socketErr.message);
    }
  }

  // Cấp sessionId mới cho Phiên 2 đăng nhập thành công
  clearUserHeartbeat(user.id);
  const sessionId = crypto.randomUUID();
  user.session_id = sessionId;
  await user.save();
  recordUserHeartbeat(user.id);

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

/**
 * Đăng nhập bằng Google kèm quy tắc Whitelist:
 * 1. Nhận thông tin Google Token (credential / idToken) hoặc email (chế độ dev/test)
 * 2. Xác thực tính hợp lệ của Google ID Token với máy chủ Google
 * 3. Kiểm tra email trong cơ sở dữ liệu (users table)
 * 4. Nếu email KHÔNG tồn tại trong danh sách cấp phép -> Từ chối truy cập (403 EMAIL_NOT_WHITELISTED)
 * 5. Nếu email tồn tại nhưng tài khoản bị vô hiệu hóa (!is_active) -> Từ chối truy cập (403 ACCOUNT_DISABLED)
 * 6. Nếu người dùng chọn vai trò (expectedRole) -> Kiểm tra tính tương thích của vai trò
 * 7. Kick các phiên đăng nhập cũ (Single Active Session)
 * 8. Cấp sessionId mới và tạo JWT Access Token trả về cho người dùng
 *
 * @param {Object} options
 * @param {string} [options.credential] Google ID Token từ GIS
 * @param {string} [options.idToken] Google ID Token
 * @param {string} [options.email] Email Google (dùng khi dev/test hoặc đã xác thực)
 * @param {string} [options.expectedRole] Vai trò mong muốn đăng nhập
 * @param {boolean} [options.rememberMe=false]
 * @returns {Promise<{ token: string, user: Object }>}
 */
async function loginWithGoogle({ credential, idToken, accessToken, email, expectedRole = null, rememberMe = false }) {
  let verifiedEmail = null;
  const tokenToVerify = credential || idToken;

  if (tokenToVerify) {
    if (tokenToVerify === 'mock-google-token' || tokenToVerify.startsWith('mock-')) {
      // Mock token dành cho test/dev
      verifiedEmail = email || 'driver01@gmail.com';
    } else {
      // Xác thực ID Token với Google OAuth2 API
      try {
        const response = await fetch(`https://oauth2.googleapis.com/tokeninfo?id_token=${encodeURIComponent(tokenToVerify)}`, {
          method: 'GET',
          headers: { Accept: 'application/json' },
        });

        if (!response.ok) {
          const errData = await response.json().catch(() => ({}));
          const err = new Error(errData.error_description || 'Google ID Token không hợp lệ hoặc đã hết hạn.');
          err.statusCode = 401;
          err.code = 'INVALID_GOOGLE_TOKEN';
          throw err;
        }

        const tokenInfo = await response.json();
        if (tokenInfo.email_verified !== 'true' && tokenInfo.email_verified !== true) {
          const err = new Error('Tài khoản Google chưa được xác thực địa chỉ email.');
          err.statusCode = 401;
          err.code = 'UNVERIFIED_GOOGLE_EMAIL';
          throw err;
        }

        verifiedEmail = tokenInfo.email;
      } catch (err) {
        if (err.statusCode) throw err;
        const fetchErr = new Error(`Lỗi kết nối xác thực với Google: ${err.message}`);
        fetchErr.statusCode = 502;
        throw fetchErr;
      }
    }
  } else if (accessToken) {
    if (accessToken === 'mock-google-access-token' || accessToken.startsWith('mock-')) {
      verifiedEmail = email || 'driver01@gmail.com';
    } else {
      // Xác thực Access Token với Google Userinfo API
      try {
        const response = await fetch('https://www.googleapis.com/oauth2/v3/userinfo', {
          method: 'GET',
          headers: {
            Authorization: `Bearer ${accessToken}`,
            Accept: 'application/json',
          },
        });

        if (!response.ok) {
          const errData = await response.json().catch(() => ({}));
          const err = new Error(errData.error_description || 'Google Access Token không hợp lệ hoặc đã hết hạn.');
          err.statusCode = 401;
          err.code = 'INVALID_GOOGLE_TOKEN';
          throw err;
        }

        const userInfo = await response.json();
        if (userInfo.email_verified !== 'true' && userInfo.email_verified !== true) {
          const err = new Error('Tài khoản Google chưa được xác thực địa chỉ email.');
          err.statusCode = 401;
          err.code = 'UNVERIFIED_GOOGLE_EMAIL';
          throw err;
        }

        verifiedEmail = userInfo.email;
      } catch (err) {
        if (err.statusCode) throw err;
        const fetchErr = new Error(`Lỗi kết nối xác thực thông tin tài khoản Google: ${err.message}`);
        fetchErr.statusCode = 502;
        throw fetchErr;
      }
    }
  } else if (email) {
    // Trường hợp dev/test gửi email trực tiếp
    verifiedEmail = email;
  }

  if (!verifiedEmail) {
    const err = new Error('Không nhận được email hợp lệ từ Google.');
    err.statusCode = 400;
    throw err;
  }

  // 4. KIỂM TRA WHITELIST TRONG CƠ SỞ DỮ LIỆU
  const user = await User.findOne({
    where: {
      email: { [Op.iLike]: verifiedEmail.trim() },
    },
  });

  // Nếu email không tồn tại trong danh sách tài khoản đã được cấp phép -> TỪ CHỐI
  if (!user) {
    const err = new Error(
      `Email "${verifiedEmail}" chưa được cấp phép truy cập vào hệ thống NexusPort. Vui lòng liên hệ Quản trị viên để được thêm vào danh sách Whitelist.`
    );
    err.statusCode = 403;
    err.code = 'EMAIL_NOT_WHITELISTED';
    throw err;
  }

  // 5. Nếu email tồn tại nhưng tài khoản đang bị khóa/vô hiệu hóa -> TỪ CHỐI
  if (!user.is_active) {
    const err = new Error('Tài khoản của bạn đã bị vô hiệu hóa. Vui lòng liên hệ Quản trị viên.');
    err.statusCode = 403;
    err.code = 'ACCOUNT_DISABLED';
    throw err;
  }

  // 6. Kiểm tra vai trò mong muốn (nếu client gửi expectedRole)
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
      (['berth', 'berth staff', 'shipping line', 'shippingline', 'hãng tàu'].includes(normExpected) &&
        ['berth', 'berth staff', 'shipping line', 'shippingline', 'hãng tàu'].includes(normActual));

    if (!isRoleMatch) {
      const err = new Error(
        `Tài khoản "${user.email}" có vai trò là "${user.role}", không thể đăng nhập với vai trò "${expectedRole}". Vui lòng chọn đúng vai trò.`
      );
      err.statusCode = 422;
      throw err;
    }
  }

  // 7. Xử lý Single Active Session: Kick phiên cũ nếu có
  if (isUserCurrentlyActive(user.id) || user.session_id) {
    console.warn(`[AuthService] Tài khoản Google "${user.email}" (${user.id}) có phiên mới. Cưỡng chế kick phiên cũ ra khỏi hệ thống.`);
    try {
      kickUserSockets(
        user.id,
        'CONCURRENT_LOGIN',
        'Tài khoản của bạn vừa được đăng nhập trên một thiết bị hoặc trình duyệt khác qua Google. Để bảo vệ an toàn tài khoản, phiên làm việc trước đó đã tự động đăng xuất.'
      );
    } catch (socketErr) {
      console.error('[AuthService] Lỗi khi kickUserSockets:', socketErr.message);
    }
  }

  clearUserHeartbeat(user.id);
  const sessionId = crypto.randomUUID();
  user.session_id = sessionId;
  await user.save();
  recordUserHeartbeat(user.id);

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

  const payload = {
    id: user.id,
    sub: user.id,
    "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier": user.id,
    username: user.username,
    name: user.full_name || user.username,
    email: user.email,
    role: user.role,
    sessionId: sessionId,
    "http://schemas.microsoft.com/ws/2008/06/identity/claims/role": user.role,
    ...(carrierId && { CarrierId: carrierId }),
  };

  const tokenExpiry = rememberMe ? '30d' : '8h';
  const token = generateToken(payload, tokenExpiry);

  return {
    token,
    user: user.toSafeObject(),
  };
}

module.exports = {
  login,
  loginWithGoogle,
  forgotPassword,
  resetPassword,
  recordUserHeartbeat,
  clearUserHeartbeat,
  isUserCurrentlyActive,
};

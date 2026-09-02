'use strict';

const bcrypt = require('bcryptjs');
const { Op } = require('sequelize');
const { User, VALID_ROLES } = require('../../models/User');
const { bcrypt: bcryptConfig } = require('../../config/env');

/**
 * Lấy danh sách người dùng với tìm kiếm, lọc và phân trang.
 *
 * @param {Object} query
 * @param {string}  [query.search]   - Tìm theo username / email / full_name
 * @param {string}  [query.role]     - Lọc theo role
 * @param {string}  [query.status]   - 'active' | 'inactive'
 * @param {number}  [query.page=1]
 * @param {number}  [query.limit=20]
 * @returns {Promise<{ users: Object[], total: number, page: number, limit: number, totalPages: number }>}
 */
async function getUsers({ search, role, status, page = 1, limit = 20 } = {}) {
  const where = {};

  // Tìm kiếm theo username, email, full_name
  if (search && search.trim()) {
    where[Op.or] = [
      { username: { [Op.iLike]: `%${search.trim()}%` } },
      { email:    { [Op.iLike]: `%${search.trim()}%` } },
      { full_name: { [Op.iLike]: `%${search.trim()}%` } },
    ];
  }

  // Lọc theo role
  if (role && role !== 'all') {
    where.role = role;
  }

  // Lọc theo trạng thái
  if (status === 'active') {
    where.is_active = true;
  } else if (status === 'inactive') {
    where.is_active = false;
  }

  const pageNum  = Math.max(1, parseInt(page, 10) || 1);
  const limitNum = Math.min(100, Math.max(1, parseInt(limit, 10) || 20));
  const offset   = (pageNum - 1) * limitNum;

  const { count, rows } = await User.findAndCountAll({
    where,
    order: [['created_at', 'DESC']],
    limit: limitNum,
    offset,
    attributes: { exclude: ['password', 'reset_token', 'reset_token_exp'] },
  });

  return {
    users:      rows.map((u) => u.toSafeObject()),
    total:      count,
    page:       pageNum,
    limit:      limitNum,
    totalPages: Math.ceil(count / limitNum),
  };
}

/**
 * Lấy chi tiết một người dùng theo ID.
 *
 * @param {string} id - UUID
 * @returns {Promise<Object>}
 * @throws {{ statusCode: 404 }}
 */
async function getUserById(id) {
  const user = await User.findByPk(id, {
    attributes: { exclude: ['password', 'reset_token', 'reset_token_exp'] },
  });

  if (!user) {
    const err = new Error('Không tìm thấy người dùng.');
    err.statusCode = 404;
    throw err;
  }

  return user.toSafeObject();
}

/**
 * Tạo tài khoản người dùng mới.
 *
 * @param {Object} dto
 * @param {string} dto.username
 * @param {string} dto.email
 * @param {string} dto.password
 * @param {string} dto.role
 * @param {string} [dto.fullName]
 * @param {boolean} [dto.isActive=true]
 * @returns {Promise<Object>}
 * @throws {{ statusCode: 409 }} Trùng email/username
 */
async function createUser({ username, email, password, role, fullName, isActive = true }) {
  // Kiểm tra username trùng
  const existingUsername = await User.findOne({
    where: { username: { [Op.iLike]: username } },
  });
  if (existingUsername) {
    const err = new Error('Username đã tồn tại.');
    err.statusCode = 409;
    throw err;
  }

  // Kiểm tra email trùng
  const existingEmail = await User.findOne({
    where: { email: { [Op.iLike]: email } },
  });
  if (existingEmail) {
    const err = new Error('Email đã tồn tại.');
    err.statusCode = 409;
    throw err;
  }

  // Hash mật khẩu
  const hashedPassword = await bcrypt.hash(password, bcryptConfig.saltRounds);

  const user = await User.create({
    username,
    email:     email.toLowerCase(),
    password:  hashedPassword,
    role,
    full_name: fullName || null,
    is_active: isActive,
  });

  return user.toSafeObject();
}

/**
 * Cập nhật thông tin người dùng.
 * Không cho phép thay đổi password qua endpoint này.
 *
 * @param {string} id
 * @param {Object} dto
 * @param {string} [dto.fullName]
 * @param {string} [dto.email]
 * @param {string} [dto.username]
 * @returns {Promise<Object>}
 * @throws {{ statusCode: 404 | 409 }}
 */
async function updateUser(id, { fullName, email, username }) {
  const user = await User.findByPk(id);
  if (!user) {
    const err = new Error('Không tìm thấy người dùng.');
    err.statusCode = 404;
    throw err;
  }

  // Kiểm tra trùng email (với user khác)
  if (email && email.toLowerCase() !== user.email.toLowerCase()) {
    const existing = await User.findOne({
      where: {
        email: { [Op.iLike]: email },
        id: { [Op.ne]: id },
      },
    });
    if (existing) {
      const err = new Error('Email đã được sử dụng bởi tài khoản khác.');
      err.statusCode = 409;
      throw err;
    }
    user.email = email.toLowerCase();
  }

  // Kiểm tra trùng username (với user khác)
  if (username && username !== user.username) {
    const existing = await User.findOne({
      where: {
        username: { [Op.iLike]: username },
        id: { [Op.ne]: id },
      },
    });
    if (existing) {
      const err = new Error('Username đã được sử dụng bởi tài khoản khác.');
      err.statusCode = 409;
      throw err;
    }
    user.username = username;
  }

  if (fullName !== undefined) {
    user.full_name = fullName || null;
  }

  await user.save();
  return user.toSafeObject();
}

/**
 * Gán role mới cho người dùng.
 *
 * @param {string} id
 * @param {string} role - Phải thuộc VALID_ROLES
 * @returns {Promise<Object>}
 * @throws {{ statusCode: 400 | 404 }}
 */
async function assignRole(id, role) {
  if (!VALID_ROLES.includes(role)) {
    const err = new Error(`Role không hợp lệ. Phải là một trong: ${VALID_ROLES.join(', ')}`);
    err.statusCode = 400;
    throw err;
  }

  const user = await User.findByPk(id);
  if (!user) {
    const err = new Error('Không tìm thấy người dùng.');
    err.statusCode = 404;
    throw err;
  }

  user.role = role;
  await user.save();
  return user.toSafeObject();
}

/**
 * Kích hoạt tài khoản người dùng.
 *
 * @param {string} id
 * @returns {Promise<Object>}
 * @throws {{ statusCode: 404 | 400 }}
 */
async function activateUser(id) {
  const user = await User.findByPk(id);
  if (!user) {
    const err = new Error('Không tìm thấy người dùng.');
    err.statusCode = 404;
    throw err;
  }

  if (user.is_active) {
    const err = new Error('Tài khoản đã đang ở trạng thái kích hoạt.');
    err.statusCode = 400;
    throw err;
  }

  user.is_active = true;
  await user.save();
  return user.toSafeObject();
}

/**
 * Vô hiệu hóa tài khoản người dùng.
 * Administrator không thể tự vô hiệu hóa tài khoản của chính mình.
 *
 * @param {string} id
 * @param {string} requesterId - ID của người thực hiện yêu cầu
 * @returns {Promise<Object>}
 * @throws {{ statusCode: 400 | 404 }}
 */
async function deactivateUser(id, requesterId) {
  if (id === requesterId) {
    const err = new Error('Bạn không thể vô hiệu hóa tài khoản của chính mình.');
    err.statusCode = 400;
    throw err;
  }

  const user = await User.findByPk(id);
  if (!user) {
    const err = new Error('Không tìm thấy người dùng.');
    err.statusCode = 404;
    throw err;
  }

  if (!user.is_active) {
    const err = new Error('Tài khoản đã ở trạng thái vô hiệu hóa.');
    err.statusCode = 400;
    throw err;
  }

  user.is_active = false;
  await user.save();
  return user.toSafeObject();
}

module.exports = {
  getUsers,
  getUserById,
  createUser,
  updateUser,
  assignRole,
  activateUser,
  deactivateUser,
};

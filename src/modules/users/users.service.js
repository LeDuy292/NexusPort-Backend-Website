'use strict';

const bcrypt = require('bcryptjs');
const { Op } = require('sequelize');
const { User, VALID_ROLES } = require('../../models/User');
const { bcrypt: bcryptConfig } = require('../../config/env');
const { sequelize } = require('../../config/database');
const { assignDriverProfile, getUserDriverAssignments, removeDriverAssignment } = require('./driver-assignment.service');

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

  const assignments = await getUserDriverAssignments(rows.filter(user => user.role === 'Driver').map(user => user.id));
  return {
    users:      rows.map((u) => ({ ...u.toSafeObject(), ...assignments.find(item => item.userId === u.id) })),
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

  const assignments = user.role === 'Driver' ? await getUserDriverAssignments([user.id]) : [];
  return { ...user.toSafeObject(), ...assignments[0] };
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
async function createUser({ username, email, password, role, fullName, isActive = true, carrierId, driverId }) {
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

  const user = await sequelize.transaction(async transaction => {
  const created = await User.create({
    username,
    email:     email.toLowerCase(),
    password:  hashedPassword,
    role,
    full_name: fullName || null,
    is_active: isActive,
  }, { transaction });
  if (role === 'Driver' && carrierId && driverId) {
    try {
      await assignDriverProfile(created.id, { carrierId, driverId }, transaction);
    } catch (err) {
      console.warn('[UsersService] assignDriverProfile warning:', err.message);
    }
  }
  return created;
  });

  return getUserById(user.id);
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
async function updateUser(id, { fullName, email, username, carrierId, driverId }) {
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

  await sequelize.transaction(async transaction => {
    await user.save({ transaction });
    if (user.role === 'Driver' && (carrierId !== undefined || driverId !== undefined)) {
      await assignDriverProfile(user.id, { carrierId, driverId }, transaction);
    }
  });
  return getUserById(user.id);
}

/**
 * Gán role mới cho người dùng.
 *
 * @param {string} id
 * @param {string} role - Phải thuộc VALID_ROLES
 * @returns {Promise<Object>}
 * @throws {{ statusCode: 400 | 404 }}
 */
async function assignRole(id, role, { carrierId, driverId } = {}) {
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
  await sequelize.transaction(async transaction => {
    await user.save({ transaction });
    if (role === 'Driver') await assignDriverProfile(user.id, { carrierId, driverId }, transaction);
    else await removeDriverAssignment(user.id, transaction);
  });
  return getUserById(user.id);
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

/**
 * Tạo tài khoản nhân viên cho công ty vận tải.
 */
async function createCarrierStaff({ username, email, password, fullName, carrierId }) {
  // Tạo user
  const user = await createUser({
    username,
    email,
    password,
    role: 'Carrier Staff',
    fullName,
    isActive: true,
  });

  // Gắn vào carrier_users
  const { sequelize } = require('../../config/database');
  await sequelize.query(
    'INSERT INTO carrier_users (carrier_id, user_id) VALUES (:carrierId, :userId)',
    {
      replacements: { carrierId, userId: user.id },
    }
  );

  return user;
}

/**
 * Lấy danh sách nhân viên của một công ty vận tải.
 */
async function getCarrierStaffs(carrierId) {
  const { sequelize } = require('../../config/database');
  const [results] = await sequelize.query(`
    SELECT u.id, u.username, u.email, u.full_name as "fullName", u.is_active as "isActive", u.created_at as "createdAt"
    FROM users u
    INNER JOIN carrier_users cu ON u.id = cu.user_id
    WHERE cu.carrier_id = :carrierId AND u.role = 'Carrier Staff'
    ORDER BY u.created_at DESC
  `, {
    replacements: { carrierId }
  });
  return results;
}


/**
 * Tạo và cấp tài khoản đăng nhập cho tài xế (vai trò Driver).
 */
async function createDriverAccount({ email, username, password, fullName, driverId, carrierId }) {
  const rawId = (username || email || '').trim();
  const isEmail = rawId.includes('@');
  const normEmail = isEmail ? rawId.toLowerCase() : `${rawId.toLowerCase()}@nexusport.vn`;
  const normUsername = rawId.toLowerCase();
  let finalCarrierId = carrierId;
  let finalDriverId = driverId;

  if (finalDriverId) {
    const [driverRow] = await sequelize.query(
      'SELECT id, carrier_id, full_name, user_id FROM drivers WHERE id = :driverId LIMIT 1',
      { replacements: { driverId: finalDriverId }, type: sequelize.QueryTypes.SELECT }
    );
    if (driverRow) {
      if (!finalCarrierId) finalCarrierId = driverRow.carrier_id;
      if (!fullName) fullName = driverRow.full_name;
    }
  }

  if (!finalCarrierId) {
    const [carrierRow] = await sequelize.query(
      "SELECT id FROM carriers WHERE company_name ILIKE '%Tiên Sa%' OR company_name ILIKE '%Cảng%' LIMIT 1",
      { type: sequelize.QueryTypes.SELECT }
    );
    if (carrierRow) finalCarrierId = carrierRow.id;
  }

  const existingUser = await User.findOne({
    where: {
      [Op.or]: [
        { email: { [Op.iLike]: normEmail } },
        { username: { [Op.iLike]: normEmail } },
      ],
    },
  });

  if (existingUser) {
    if (existingUser.role === 'Driver') {
      const hashedPassword = await bcrypt.hash(password, bcryptConfig.saltRounds);
      existingUser.password = hashedPassword;
      existingUser.is_active = true;
      if (fullName) existingUser.full_name = fullName;
      await existingUser.save();
      if (finalDriverId && finalCarrierId) {
        await assignDriverProfile(existingUser.id, { carrierId: finalCarrierId, driverId: finalDriverId });
      }
      return {
        user: existingUser.toSafeObject(),
        created: false,
        message: 'Đã kích hoạt và cập nhật tài khoản tài xế thành công!',
      };
    } else {
      const err = new Error('Email ' + normEmail + ' đã tồn tại cho tài khoản vai trò "' + existingUser.role + '". Vui lòng chọn email khác.');
      err.statusCode = 409;
      throw err;
    }
  }

  const createdUser = await createUser({
    username: normUsername,
    email: normEmail,
    password: effectivePassword,
    role: 'Driver',
    fullName: fullName || 'Tài xế',
    isActive: true,
    carrierId: finalCarrierId,
    driverId: finalDriverId,
  });

  return {
    user: createdUser,
    created: true,
    message: 'Đã cấp tài khoản và mật khẩu thành công cho tài xế!',
  };
}

/**
 * Cấp hoặc cập nhật tài khoản và mật khẩu cho Transport Company
 */
async function createCarrierAccount({ email, username, password, fullName, companyName, carrierId }) {
  const rawId = (username || email || '').trim();
  const isEmail = rawId.includes('@');
  const normEmail = isEmail ? rawId.toLowerCase() : `${rawId.toLowerCase()}@nexusport.vn`;
  const normUsername = rawId.toLowerCase();
  let finalCarrierId = carrierId;

  if (!finalCarrierId && companyName) {
    try {
      const [cRow] = await sequelize.query(
        'SELECT id FROM carriers WHERE company_name ILIKE :comp LIMIT 1',
        { replacements: { comp: `%${companyName}%` }, type: sequelize.QueryTypes.SELECT }
      );
      if (cRow) finalCarrierId = cRow.id;
    } catch (_) {}
  }

  const existingUser = await User.findOne({
    where: {
      [Op.or]: [
        { email: { [Op.iLike]: normEmail } },
        { username: { [Op.iLike]: normUsername } },
      ],
    },
  });

  const DEFAULT_SYSTEM_PASS = 'NexusPort@2026';
  const effectivePassword = password || DEFAULT_SYSTEM_PASS;

  if (existingUser) {
    if (['Transport Company', 'Carrier', 'Carrier Staff'].includes(existingUser.role)) {
      if (password) {
        const hashedPassword = await bcrypt.hash(password, bcryptConfig.saltRounds);
        existingUser.password = hashedPassword;
      }
      existingUser.is_active = true;
      if (fullName || companyName) existingUser.full_name = fullName || companyName;
      await existingUser.save();

      if (finalCarrierId) {
        try {
          const [existingCU] = await sequelize.query(
            'SELECT id FROM carrier_users WHERE carrier_id = :carrierId AND user_id = :userId LIMIT 1',
            { replacements: { carrierId: finalCarrierId, userId: existingUser.id }, type: sequelize.QueryTypes.SELECT }
          );
          if (!existingCU) {
            await sequelize.query(
              'INSERT INTO carrier_users (carrier_id, user_id) VALUES (:carrierId, :userId)',
              { replacements: { carrierId: finalCarrierId, userId: existingUser.id }, type: sequelize.QueryTypes.INSERT }
            );
          }
        } catch (_) {}
      }

      return {
        user: existingUser.toSafeObject(),
        created: false,
        message: 'Đã cập nhật mật khẩu và kích hoạt tài khoản Transport Company thành công!',
      };
    } else {
      const err = new Error('Tài khoản ' + rawId + ' đã tồn tại với vai trò "' + existingUser.role + '". Vui lòng chọn tên đăng nhập khác.');
      err.statusCode = 409;
      throw err;
    }
  }

  const createdUser = await createUser({
    username: normUsername,
    email: normEmail,
    password: effectivePassword,
    role: 'Transport Company',
    fullName: fullName || companyName || 'Đơn vị Vận tải',
    isActive: true,
  });

  if (finalCarrierId) {
    try {
      await sequelize.query(
        'INSERT INTO carrier_users (carrier_id, user_id) VALUES (:carrierId, :userId)',
        { replacements: { carrierId: finalCarrierId, userId: createdUser.id }, type: sequelize.QueryTypes.INSERT }
      );
    } catch (_) {}
  }

  return {
    user: createdUser,
    created: true,
    message: 'Đã cấp tài khoản và mật khẩu thành công cho đơn vị vận tải (Transport Company)!',
  };
}

module.exports = {
  getUsers,
  getUserById,
  createUser,
  updateUser,
  assignRole,
  activateUser,
  deactivateUser,
  createCarrierStaff,
  createDriverAccount,
  createCarrierAccount,
  getCarrierStaffs,
};

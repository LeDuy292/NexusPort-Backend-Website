'use strict';

const { validationResult } = require('express-validator');
const usersService = require('./users.service');

/**
 * Helper: xử lý lỗi service có statusCode
 */
function handleServiceError(error, res, next) {
  if (error.statusCode) {
    return res.status(error.statusCode).json({
      success: false,
      message: error.message,
    });
  }
  next(error);
}

/**
 * Helper: kiểm tra validation errors từ express-validator
 */
function checkValidation(req, res) {
  const errors = validationResult(req);
  if (!errors.isEmpty()) {
    res.status(422).json({
      success: false,
      message: errors.array()[0].msg,
      errors: errors.array().map((e) => ({ field: e.path, message: e.msg })),
    });
    return false;
  }
  return true;
}

/**
 * GET /api/users
 * Lấy danh sách người dùng (có tìm kiếm, lọc, phân trang).
 */
async function listUsers(req, res, next) {
  try {
    if (!checkValidation(req, res)) return;

    const { search, role, status, page, limit } = req.query;
    const result = await usersService.getUsers({ search, role, status, page, limit });

    return res.status(200).json({
      success: true,
      data: result,
    });
  } catch (error) {
    handleServiceError(error, res, next);
  }
}

/**
 * GET /api/users/:id
 * Lấy thông tin chi tiết một người dùng.
 */
async function getUserById(req, res, next) {
  try {
    const { id } = req.params;
    const user = await usersService.getUserById(id);

    return res.status(200).json({
      success: true,
      data: { user },
    });
  } catch (error) {
    handleServiceError(error, res, next);
  }
}

/**
 * POST /api/users
 * Tạo tài khoản người dùng mới.
 */
async function createUser(req, res, next) {
  try {
    if (!checkValidation(req, res)) return;

    const { username, email, password, role, fullName, isActive } = req.body;
    const user = await usersService.createUser({
      username,
      email,
      password,
      role,
      fullName,
      isActive: isActive !== undefined ? isActive : true,
    });

    return res.status(201).json({
      success: true,
      message: 'Tạo tài khoản thành công.',
      data: { user },
    });
  } catch (error) {
    handleServiceError(error, res, next);
  }
}

/**
 * PUT /api/users/:id
 * Cập nhật thông tin người dùng (fullName, email, username).
 */
async function updateUser(req, res, next) {
  try {
    if (!checkValidation(req, res)) return;

    const { id } = req.params;
    const { fullName, email, username } = req.body;
    const user = await usersService.updateUser(id, { fullName, email, username });

    return res.status(200).json({
      success: true,
      message: 'Cập nhật thông tin thành công.',
      data: { user },
    });
  } catch (error) {
    handleServiceError(error, res, next);
  }
}

/**
 * PATCH /api/users/:id/assign-role
 * Gán role mới cho người dùng.
 */
async function assignRole(req, res, next) {
  try {
    if (!checkValidation(req, res)) return;

    const { id } = req.params;
    const { role } = req.body;
    const user = await usersService.assignRole(id, role);

    return res.status(200).json({
      success: true,
      message: `Đã gán role "${role}" cho tài khoản thành công.`,
      data: { user },
    });
  } catch (error) {
    handleServiceError(error, res, next);
  }
}

/**
 * PATCH /api/users/:id/activate
 * Kích hoạt tài khoản người dùng.
 */
async function activateUser(req, res, next) {
  try {
    const { id } = req.params;
    const user = await usersService.activateUser(id);

    return res.status(200).json({
      success: true,
      message: 'Tài khoản đã được kích hoạt.',
      data: { user },
    });
  } catch (error) {
    handleServiceError(error, res, next);
  }
}

/**
 * PATCH /api/users/:id/deactivate
 * Vô hiệu hóa tài khoản người dùng.
 */
async function deactivateUser(req, res, next) {
  try {
    const { id } = req.params;
    const requesterId = req.user.id;
    const user = await usersService.deactivateUser(id, requesterId);

    return res.status(200).json({
      success: true,
      message: 'Tài khoản đã bị vô hiệu hóa.',
      data: { user },
    });
  } catch (error) {
    handleServiceError(error, res, next);
  }
}

module.exports = {
  listUsers,
  getUserById,
  createUser,
  updateUser,
  assignRole,
  activateUser,
  deactivateUser,
};

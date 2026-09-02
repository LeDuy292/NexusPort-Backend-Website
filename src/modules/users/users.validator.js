'use strict';

const { body, query } = require('express-validator');
const { VALID_ROLES } = require('../../models/User');

/**
 * Validate query params cho GET /api/users
 */
const queryValidator = [
  query('page')
    .optional()
    .isInt({ min: 1 })
    .withMessage('Số trang phải là số nguyên dương.')
    .toInt(),

  query('limit')
    .optional()
    .isInt({ min: 1, max: 100 })
    .withMessage('Giới hạn phải từ 1–100.')
    .toInt(),

  query('status')
    .optional()
    .isIn(['active', 'inactive', 'all'])
    .withMessage('Status phải là: active, inactive hoặc all.'),

  query('role')
    .optional(),
];

/**
 * Validate body cho POST /api/users (tạo tài khoản mới)
 */
const createUserValidator = [
  body('username')
    .trim()
    .notEmpty().withMessage('Username không được để trống.')
    .isLength({ min: 3, max: 100 }).withMessage('Username phải từ 3–100 ký tự.')
    .matches(/^[a-zA-Z0-9_.-]+$/).withMessage('Username chỉ chứa chữ cái, số, dấu gạch dưới, chấm hoặc gạch ngang.'),

  body('email')
    .trim()
    .notEmpty().withMessage('Email không được để trống.')
    .isEmail().withMessage('Email không hợp lệ.')
    .normalizeEmail({ gmail_remove_dots: false }),

  body('password')
    .notEmpty().withMessage('Mật khẩu không được để trống.')
    .isLength({ min: 6 }).withMessage('Mật khẩu phải có ít nhất 6 ký tự.'),

  body('role')
    .notEmpty().withMessage('Role không được để trống.')
    .isIn(VALID_ROLES).withMessage(`Role phải là một trong: ${VALID_ROLES.join(', ')}`),

  body('fullName')
    .optional({ nullable: true })
    .trim()
    .isLength({ max: 255 }).withMessage('Họ và tên không quá 255 ký tự.'),

  body('isActive')
    .optional()
    .isBoolean().withMessage('isActive phải là boolean.'),
];

/**
 * Validate body cho PUT /api/users/:id (cập nhật thông tin)
 */
const updateUserValidator = [
  body('username')
    .optional()
    .trim()
    .isLength({ min: 3, max: 100 }).withMessage('Username phải từ 3–100 ký tự.')
    .matches(/^[a-zA-Z0-9_.-]+$/).withMessage('Username chỉ chứa chữ cái, số, dấu gạch dưới, chấm hoặc gạch ngang.'),

  body('email')
    .optional()
    .trim()
    .isEmail().withMessage('Email không hợp lệ.')
    .normalizeEmail({ gmail_remove_dots: false }),

  body('fullName')
    .optional({ nullable: true })
    .trim()
    .isLength({ max: 255 }).withMessage('Họ và tên không quá 255 ký tự.'),
];

/**
 * Validate body cho PATCH /api/users/:id/assign-role
 */
const assignRoleValidator = [
  body('role')
    .notEmpty().withMessage('Role không được để trống.')
    .isIn(VALID_ROLES).withMessage(`Role phải là một trong: ${VALID_ROLES.join(', ')}`),
];

module.exports = {
  queryValidator,
  createUserValidator,
  updateUserValidator,
  assignRoleValidator,
};

'use strict';

const { body } = require('express-validator');

/**
 * Validate request body cho POST /api/auth/login
 */
const loginValidator = [
  body('username')
    .trim()
    .notEmpty()
    .withMessage('Tên đăng nhập hoặc email không được để trống.')
    .isLength({ min: 3 })
    .withMessage('Tên đăng nhập hoặc email phải có ít nhất 3 ký tự.'),

  body('password')
    .notEmpty()
    .withMessage('Mật khẩu không được để trống.')
    .isLength({ min: 6 })
    .withMessage('Mật khẩu phải có ít nhất 6 ký tự.'),

  body('expectedRole')
    .optional()
    .trim(),
];

const forgotPasswordValidator = [
  body('email')
    .trim()
    .notEmpty()
    .withMessage('Vui lòng nhập Email hoặc Tên đăng nhập.'),
];

const resetPasswordValidator = [
  body('email')
    .trim()
    .notEmpty()
    .withMessage('Vui lòng nhập Email hoặc Tên đăng nhập.'),
  body('otp')
    .trim()
    .notEmpty()
    .withMessage('Vui lòng nhập mã OTP xác thực.')
    .isLength({ min: 6, max: 6 })
    .withMessage('Mã OTP gồm 6 chữ số.'),
  body('newPassword')
    .notEmpty()
    .withMessage('Vui lòng nhập mật khẩu mới.')
    .isLength({ min: 6 })
    .withMessage('Mật khẩu mới phải có ít nhất 6 ký tự.'),
];

const googleLoginValidator = [
  body().custom((val, { req }) => {
    const { credential, idToken, accessToken, email } = req.body || {};
    if (!credential && !idToken && !accessToken && !email) {
      throw new Error('Vui lòng cung cấp Google credential (idToken), accessToken hoặc email.');
    }
    return true;
  }),
  body('email')
    .optional()
    .isEmail()
    .withMessage('Email không hợp lệ.'),
  body('expectedRole')
    .optional()
    .trim(),
];

module.exports = { loginValidator, googleLoginValidator, forgotPasswordValidator, resetPasswordValidator };

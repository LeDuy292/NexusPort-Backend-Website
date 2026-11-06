'use strict';

const { Router } = require('express');
const { loginController, googleLoginController, logoutController, getMeController, forgotPasswordController, resetPasswordController } = require('./auth.controller');
const { loginValidator, googleLoginValidator, forgotPasswordValidator, resetPasswordValidator } = require('./auth.validator');
const { authenticate } = require('../../middlewares/authenticate');

const router = Router();

/**
 * @swagger
 * tags:
 *   name: Auth
 *   description: Xác thực người dùng
 */

/**
 * @swagger
 * /api/auth/login:
 *   post:
 *     summary: Đăng nhập vào hệ thống
 *     tags: [Auth]
 *     requestBody:
 *       required: true
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             required:
 *               - username
 *               - password
 *             properties:
 *               username:
 *                 type: string
 *                 description: Username hoặc email của tài khoản
 *                 example: dispatcher01
 *               password:
 *                 type: string
 *                 description: Mật khẩu tài khoản
 *                 example: NexusPort@2026
 *     responses:
 *       200:
 *         description: Đăng nhập thành công
 *         content:
 *           application/json:
 *             schema:
 *               type: object
 *               properties:
 *                 success:
 *                   type: boolean
 *                   example: true
 *                 message:
 *                   type: string
 *                   example: Đăng nhập thành công.
 *                 data:
 *                   type: object
 *                   properties:
 *                     token:
 *                       type: string
 *                       description: JWT Access Token (8 giờ)
 *                     user:
 *                       $ref: '#/components/schemas/UserSafe'
 *       401:
 *         description: Thông tin đăng nhập không chính xác
 *       403:
 *         description: Tài khoản bị vô hiệu hóa
 *       422:
 *         description: Dữ liệu đầu vào không hợp lệ
 */
router.post('/login', loginValidator, loginController);
/**
 * @swagger
 * /api/auth/google:
 *   post:
 *     summary: Đăng nhập bằng Google (chỉ các email trong Whitelist mới được cấp phép)
 *     tags: [Auth]
 *     requestBody:
 *       required: true
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             properties:
 *               credential:
 *                 type: string
 *                 description: Google ID Token (từ Google Identity Services)
 *               idToken:
 *                 type: string
 *                 description: Google ID Token thay thế
 *               email:
 *                 type: string
 *                 description: Email Google (hỗ trợ cho môi trường dev/test)
 *                 example: driver01@gmail.com
 *               expectedRole:
 *                 type: string
 *                 description: Vai trò mong muốn đăng nhập
 *               rememberMe:
 *                 type: boolean
 *     responses:
 *       200:
 *         description: Đăng nhập Google thành công
 *         content:
 *           application/json:
 *             schema:
 *               type: object
 *               properties:
 *                 success:
 *                   type: boolean
 *                   example: true
 *                 message:
 *                   type: string
 *                   example: Đăng nhập Google thành công.
 *                 data:
 *                   type: object
 *                   properties:
 *                     token:
 *                       type: string
 *                     user:
 *                       $ref: '#/components/schemas/UserSafe'
 *       401:
 *         description: Google token không hợp lệ
 *       403:
 *         description: Email không thuộc Whitelist hoặc tài khoản bị vô hiệu hóa
 *       422:
 *         description: Dữ liệu không hợp lệ hoặc sai vai trò
 */
router.post('/google', googleLoginValidator, googleLoginController);
/**
 * @swagger
 * /api/auth/logout:
 *   post:
 *     summary: Đăng xuất khỏi hệ thống
 *     tags: [Auth]
 *     security:
 *       - BearerAuth: []
 *     responses:
 *       200:
 *         description: Đăng xuất thành công
 *         content:
 *           application/json:
 *             schema:
 *               type: object
 *               properties:
 *                 success:
 *                   type: boolean
 *                   example: true
 *                 message:
 *                   type: string
 *                   example: Đăng xuất thành công.
 *       401:
 *         description: Chưa xác thực hoặc token không hợp lệ
 */
router.post('/logout', logoutController);
router.post('/forgot-password', forgotPasswordValidator, forgotPasswordController);
router.post('/reset-password', resetPasswordValidator, resetPasswordController);

/**
 * @swagger
 * /api/auth/me:
 *   get:
 *     summary: Lấy thông tin người dùng hiện tại
 *     tags: [Auth]
 *     security:
 *       - BearerAuth: []
 *     responses:
 *       200:
 *         description: Thông tin user
 *         content:
 *           application/json:
 *             schema:
 *               type: object
 *               properties:
 *                 success:
 *                   type: boolean
 *                 data:
 *                   type: object
 *                   properties:
 *                     user:
 *                       $ref: '#/components/schemas/UserSafe'
 *       401:
 *         description: Chưa xác thực hoặc token hết hạn
 */
router.get('/me', authenticate, getMeController);

module.exports = router;

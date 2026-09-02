'use strict';

const { Router } = require('express');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const { PERMISSIONS } = require('../../config/rbac');
const {
  listUsers,
  getUserById,
  createUser,
  updateUser,
  assignRole,
  activateUser,
  deactivateUser,
} = require('./users.controller');
const {
  queryValidator,
  createUserValidator,
  updateUserValidator,
  assignRoleValidator,
} = require('./users.validator');

const router = Router();

// Tất cả routes trong module này yêu cầu: đã đăng nhập + là Administrator
const adminGuard = [authenticate, authorize(PERMISSIONS.ADMIN_ONLY)];

/**
 * @swagger
 * tags:
 *   name: Users
 *   description: Quản lý tài khoản người dùng (Administrator only)
 */

/**
 * @swagger
 * /api/users:
 *   get:
 *     summary: Lấy danh sách người dùng (có tìm kiếm, lọc, phân trang)
 *     tags: [Users]
 *     security:
 *       - BearerAuth: []
 *     parameters:
 *       - in: query
 *         name: search
 *         schema:
 *           type: string
 *         description: Tìm theo username, email hoặc họ tên
 *       - in: query
 *         name: role
 *         schema:
 *           type: string
 *           enum: [Administrator, Transport Company, Driver, Dispatcher, Gate Officer, Yard Operator, Berth Staff]
 *         description: Lọc theo role
 *       - in: query
 *         name: status
 *         schema:
 *           type: string
 *           enum: [active, inactive, all]
 *         description: Lọc theo trạng thái tài khoản
 *       - in: query
 *         name: page
 *         schema:
 *           type: integer
 *           default: 1
 *         description: Số trang
 *       - in: query
 *         name: limit
 *         schema:
 *           type: integer
 *           default: 20
 *           maximum: 100
 *         description: Số lượng bản ghi mỗi trang
 *     responses:
 *       200:
 *         description: Danh sách người dùng
 *         content:
 *           application/json:
 *             schema:
 *               type: object
 *               properties:
 *                 success:
 *                   type: boolean
 *                   example: true
 *                 data:
 *                   type: object
 *                   properties:
 *                     users:
 *                       type: array
 *                       items:
 *                         $ref: '#/components/schemas/UserSafe'
 *                     total:
 *                       type: integer
 *                       example: 10
 *                     page:
 *                       type: integer
 *                       example: 1
 *                     limit:
 *                       type: integer
 *                       example: 20
 *                     totalPages:
 *                       type: integer
 *                       example: 1
 *       401:
 *         description: Chưa xác thực
 *       403:
 *         description: Không đủ quyền (chỉ Administrator)
 */
router.get('/', adminGuard, queryValidator, listUsers);

/**
 * @swagger
 * /api/users/{id}:
 *   get:
 *     summary: Lấy chi tiết một người dùng theo ID
 *     tags: [Users]
 *     security:
 *       - BearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema:
 *           type: string
 *           format: uuid
 *         description: UUID của người dùng
 *     responses:
 *       200:
 *         description: Thông tin chi tiết người dùng
 *         content:
 *           application/json:
 *             schema:
 *               type: object
 *               properties:
 *                 success:
 *                   type: boolean
 *                   example: true
 *                 data:
 *                   type: object
 *                   properties:
 *                     user:
 *                       $ref: '#/components/schemas/UserSafe'
 *       401:
 *         description: Chưa xác thực
 *       403:
 *         description: Không đủ quyền
 *       404:
 *         description: Không tìm thấy người dùng
 */
router.get('/:id', adminGuard, getUserById);

/**
 * @swagger
 * /api/users:
 *   post:
 *     summary: Tạo tài khoản người dùng mới
 *     tags: [Users]
 *     security:
 *       - BearerAuth: []
 *     requestBody:
 *       required: true
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             required:
 *               - username
 *               - email
 *               - password
 *               - role
 *             properties:
 *               username:
 *                 type: string
 *                 example: yard_operator_02
 *               email:
 *                 type: string
 *                 format: email
 *                 example: yard02@nexusport.vn
 *               password:
 *                 type: string
 *                 example: NexusPort@2026
 *               role:
 *                 type: string
 *                 enum: [Administrator, Transport Company, Driver, Dispatcher, Gate Officer, Yard Operator, Berth Staff]
 *                 example: Yard Operator
 *               fullName:
 *                 type: string
 *                 example: Nguyễn Văn A
 *               isActive:
 *                 type: boolean
 *                 default: true
 *     responses:
 *       201:
 *         description: Tạo tài khoản thành công
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
 *                   example: Tạo tài khoản thành công.
 *                 data:
 *                   type: object
 *                   properties:
 *                     user:
 *                       $ref: '#/components/schemas/UserSafe'
 *       401:
 *         description: Chưa xác thực
 *       403:
 *         description: Không đủ quyền
 *       409:
 *         description: Username hoặc email đã tồn tại
 *       422:
 *         description: Dữ liệu đầu vào không hợp lệ
 */
router.post('/', adminGuard, createUserValidator, createUser);

/**
 * @swagger
 * /api/users/{id}:
 *   put:
 *     summary: Cập nhật thông tin người dùng (fullName, email, username)
 *     tags: [Users]
 *     security:
 *       - BearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema:
 *           type: string
 *           format: uuid
 *     requestBody:
 *       required: true
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             properties:
 *               username:
 *                 type: string
 *                 example: yard_operator_02_updated
 *               email:
 *                 type: string
 *                 format: email
 *                 example: yard02_new@nexusport.vn
 *               fullName:
 *                 type: string
 *                 example: Nguyễn Văn B
 *     responses:
 *       200:
 *         description: Cập nhật thành công
 *       401:
 *         description: Chưa xác thực
 *       403:
 *         description: Không đủ quyền
 *       404:
 *         description: Không tìm thấy người dùng
 *       409:
 *         description: Email hoặc username đã được sử dụng bởi tài khoản khác
 *       422:
 *         description: Dữ liệu đầu vào không hợp lệ
 */
router.put('/:id', adminGuard, updateUserValidator, updateUser);

/**
 * @swagger
 * /api/users/{id}/assign-role:
 *   patch:
 *     summary: Gán role mới cho người dùng
 *     tags: [Users]
 *     security:
 *       - BearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema:
 *           type: string
 *           format: uuid
 *     requestBody:
 *       required: true
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             required:
 *               - role
 *             properties:
 *               role:
 *                 type: string
 *                 enum: [Administrator, Transport Company, Driver, Dispatcher, Gate Officer, Yard Operator, Berth Staff]
 *                 example: Dispatcher
 *     responses:
 *       200:
 *         description: Gán role thành công
 *       400:
 *         description: Role không hợp lệ
 *       401:
 *         description: Chưa xác thực
 *       403:
 *         description: Không đủ quyền
 *       404:
 *         description: Không tìm thấy người dùng
 */
router.patch('/:id/assign-role', adminGuard, assignRoleValidator, assignRole);

/**
 * @swagger
 * /api/users/{id}/activate:
 *   patch:
 *     summary: Kích hoạt tài khoản người dùng
 *     tags: [Users]
 *     security:
 *       - BearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema:
 *           type: string
 *           format: uuid
 *     responses:
 *       200:
 *         description: Kích hoạt thành công
 *       400:
 *         description: Tài khoản đã ở trạng thái kích hoạt
 *       401:
 *         description: Chưa xác thực
 *       403:
 *         description: Không đủ quyền
 *       404:
 *         description: Không tìm thấy người dùng
 */
router.patch('/:id/activate', adminGuard, activateUser);

/**
 * @swagger
 * /api/users/{id}/deactivate:
 *   patch:
 *     summary: Vô hiệu hóa tài khoản người dùng
 *     tags: [Users]
 *     security:
 *       - BearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema:
 *           type: string
 *           format: uuid
 *     responses:
 *       200:
 *         description: Vô hiệu hóa thành công
 *       400:
 *         description: Tài khoản đã bị vô hiệu hóa hoặc không thể tự vô hiệu hóa
 *       401:
 *         description: Chưa xác thực
 *       403:
 *         description: Không đủ quyền
 *       404:
 *         description: Không tìm thấy người dùng
 */
router.patch('/:id/deactivate', adminGuard, deactivateUser);

module.exports = router;

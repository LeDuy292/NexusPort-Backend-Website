'use strict';

const { Router } = require('express');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const ctrl = require('./yard.controller');

const router = Router();

// Staff-only: this response contains current physical container positions.
router.get('/layout', authenticate,
  authorize('Yard Operator', 'Dispatcher', 'Administrator', 'Gate Officer'),
  async (_req, res, next) => {
    try {
      const { getLayoutSnapshot } = require('./yard-layout');
      res.set('Cache-Control', 'no-store');
      res.json({ success: true, data: await getLayoutSnapshot() });
    } catch (error) { next(error); }
  });

/**
 * @swagger
 * /api/yard/containers:
 *   get:
 *     summary: Lấy danh sách container đang có mặt trong bãi
 *     description: Dành cho Yard Operator, Dispatcher, Administrator và Gate Officer.
 *     tags: [Yard Operations]
 *     security:
 *       - BearerAuth: []
 *     responses:
 *       200:
 *         description: Danh sách container và vị trí trong bãi.
 *       401:
 *         description: Chưa đăng nhập hoặc phiên không hợp lệ.
 *       403:
 *         description: Tài khoản không có quyền xem danh sách container trong bãi.
 */
router.get('/containers', authenticate,
  authorize('Yard Operator', 'Dispatcher', 'Administrator', 'Gate Officer'),
  async (_req, res, next) => {
    try {
      res.set('Cache-Control', 'no-store');
      res.json({ success: true, data: await require('./yard-containers').getYardContainers() });
    } catch (error) { next(error); }
  });

// Báo cáo sự cố / hư hỏng tại bãi: nhân viên bãi gửi, Dispatcher nhận và xử lý.
const incidentActor = (req) => ({
  id: req.user && req.user.id,
  role: req.user && req.user.role,
  username: (req.authenticatedAccount && req.authenticatedAccount.username) || (req.user && req.user.username),
  full_name: req.authenticatedAccount && req.authenticatedAccount.full_name,
});
const incidentHandler = (fn, status = 200) => async (req, res, next) => {
  try { res.status(status).json({ success: true, data: await fn(req) }); }
  catch (error) {
    if (error.statusCode) return res.status(error.statusCode).json({ success: false, message: error.message });
    return next(error);
  }
};
router.post('/incident-reports', authenticate, authorize('Yard Operator', 'Gate Officer', 'Administrator'),
  incidentHandler((req) => require('./yard-incidents').createReport(incidentActor(req), req.body), 201));
router.get('/incident-reports', authenticate, authorize('Dispatcher', 'Administrator'),
  incidentHandler((req) => require('./yard-incidents').listReports(req.query)));
router.patch('/incident-reports/:id', authenticate, authorize('Dispatcher', 'Administrator'),
  incidentHandler((req) => require('./yard-incidents').updateStatus(incidentActor(req), req.params.id, req.body)));

// Xung đột vị trí đặt chỗ: quét ngay (tự động vẫn chạy nền) và Dispatcher chấp nhận ô đề xuất.
router.post('/slot-conflicts/check', authenticate, authorize('Dispatcher', 'Administrator', 'Yard Operator'),
  incidentHandler(() => require('./yard-slot-conflicts').checkSlotConflicts()));
router.post('/incident-reports/:id/apply-proposal', authenticate, authorize('Dispatcher', 'Administrator'),
  incidentHandler((req) => require('./yard-slot-conflicts').applyProposal(incidentActor(req), req.params.id, req.body && req.body.slot)));

// Phí đảo container chiều xuất: Transport Company xem và thanh toán; bãi / Dispatcher theo dõi.
router.get('/shifting-charges', authenticate, authorize('Transport Company', 'Dispatcher', 'Administrator', 'Yard Operator', 'Gate Officer'),
  incidentHandler((req) => require('./yard-shifting-charges').listCharges(incidentActor(req), req.query)));
router.post('/shifting-charges/:id/payment', authenticate, authorize('Transport Company', 'Administrator'),
  incidentHandler((req) => require('./yard-shifting-charges').createPayment(incidentActor(req), req.params.id)));
router.post('/shifting-charges/:id/sync', authenticate, authorize('Transport Company', 'Administrator'),
  incidentHandler((req) => require('./yard-shifting-charges').syncPayment(incidentActor(req), req.params.id)));

router.post('/blocks',authenticate,authorize('Yard Operator','Administrator'),async(req,res,next)=>{
  try{res.status(201).json({success:true,data:await require('./yard-layout.commands').createBlock(req.body)});}catch(error){next(error);}
});
router.get('/blocks',authenticate,authorize('Yard Operator','Dispatcher','Administrator','Gate Officer'),async(_req,res,next)=>{
  try{const layout=require('./yard-layout');res.set('Cache-Control','no-store');res.json({success:true,data:layout.blockDirectory(await layout.getLayoutSnapshot())});}catch(error){next(error);}
});
router.post('/slots/:id/maintenance',authenticate,authorize('Yard Operator','Administrator'),async(req,res,next)=>{
  try{res.json({success:true,data:await require('./yard-layout.commands').setMaintenance(req.params.id,req.body.maintenance)});}catch(error){next(error);}
});

/**
 * Tra cứu lịch sử Slot và Container theo Timeline
 */
router.get(
  '/slots/history',
  authenticate,
  authorize('Yard Operator', 'Dispatcher', 'Administrator', 'Gate Officer', 'Carrier Staff', 'Transport Company'),
  ctrl.getSlotOccupancyHistory
);

/**
 * Xuất dữ liệu lịch sử Slot ra CSV làm bằng chứng
 */
router.get(
  '/slots/history/export',
  authenticate,
  authorize('Yard Operator', 'Dispatcher', 'Administrator', 'Gate Officer', 'Carrier Staff', 'Transport Company'),
  ctrl.exportSlotOccupancyHistory
);

/**
 * Ghi nhận di chuyển container giữa các slot
 */
router.post(
  '/slots/movement',
  authenticate,
  authorize('Yard Operator', 'Dispatcher', 'Administrator'),
  ctrl.recordContainerMovement
);

/**
 * @openapi
 * /api/yard/gate-out-requests:
 *   get:
 *     tags: [Yard Operations]
 *     summary: Lấy danh sách yêu cầu xuất container bãi
 *     security:
 *       - bearerAuth: []
 *     parameters:
 *       - in: query
 *         name: status
 *         schema:
 *           type: string
 *           enum: [PENDING, IN_PROGRESS, READY_FOR_GATE_OUT, COMPLETED, all]
 *       - in: query
 *         name: urgency
 *         schema:
 *           type: string
 *           enum: [CRITICAL, HIGH, MEDIUM, LOW, All]
 *       - in: query
 *         name: search
 *         schema: { type: string }
 *       - in: query
 *         name: page
 *         schema: { type: integer, default: 1 }
 *       - in: query
 *         name: limit
 *         schema: { type: integer, default: 20 }
 *     responses:
 *       200:
 *         description: Danh sách yêu cầu xuất container
 *   post:
 *     tags: [Yard Operations]
 *     summary: Tạo mới yêu cầu xuất container bãi khi xe vào cổng
 *     security:
 *       - bearerAuth: []
 *     requestBody:
 *       required: true
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             required: [containerId, driverName, truckPlate, sealNumber]
 *             properties:
 *               containerId: { type: string }
 *               driverName: { type: string }
 *               truckPlate: { type: string }
 *               sealNumber: { type: string }
 *               containerType: { type: string }
 *               cargoType: { type: string }
 *               gateName: { type: string }
 *               bookingCode: { type: string }
 *               yardBlock: { type: string }
 *               yardSlot: { type: string }
 *     responses:
 *       201:
 *         description: Yêu cầu xuất container được tạo thành công
 */
router.get(
  '/gate-out-requests',
  authenticate,
  authorize('Yard Operator', 'Administrator', 'Gate Officer', 'Dispatcher'),
  ctrl.getGateOutRequests
);

router.post(
  '/gate-out-requests',
  authenticate,
  authorize('Gate Officer', 'Dispatcher', 'Administrator', 'Yard Operator'),
  ctrl.createGateOutRequest
);

/**
 * @openapi
 * /api/yard/gate-out-requests/{id}:
 *   get:
 *     tags: [Yard Operations]
 *     summary: Xem chi tiết yêu cầu xuất container bãi
 *     security:
 *       - bearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema: { type: string, format: uuid }
 *     responses:
 *       200:
 *         description: Chi tiết yêu cầu xuất container
 *       404:
 *         description: Không tìm thấy yêu cầu
 */
router.get(
  '/gate-out-requests/:id',
  authenticate,
  authorize('Yard Operator', 'Administrator', 'Gate Officer', 'Dispatcher'),
  ctrl.getGateOutRequestById
);

/**
 * @openapi
 * /api/yard/gate-out-requests/{id}/lift-order:
 *   post:
 *     tags: [Yard Operations]
 *     summary: Tạo và nạp lệnh nâng cẩu RTG cho yêu cầu xuất bãi
 *     security:
 *       - bearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema: { type: string, format: uuid }
 *     requestBody:
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             properties:
 *               rtgCrane: { type: string }
 *               rtgOperator: { type: string }
 *     responses:
 *       200:
 *         description: Lệnh cẩu được khởi tạo và đang di chuyển
 *       404:
 *         description: Không tìm thấy yêu cầu
 */
router.post(
  '/gate-out-requests/:id/lift-order',
  authenticate,
  authorize('Yard Operator', 'Administrator'),
  ctrl.createLiftOrder
);

/**
 * @openapi
 * /api/yard/gate-out-requests/{id}/step:
 *   patch:
 *     tags: [Yard Operations]
 *     summary: Cập nhật bước tiến trình cẩu container
 *     security:
 *       - bearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema: { type: string, format: uuid }
 *     requestBody:
 *       required: true
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             required: [step]
 *             properties:
 *               step: { type: integer, enum: [1, 2, 3, 4] }
 *               note: { type: string }
 *     responses:
 *       200:
 *         description: Cập nhật tiến trình thành công
 *       404:
 *         description: Không tìm thấy yêu cầu
 */
router.patch(
  '/gate-out-requests/:id/step',
  authenticate,
  authorize('Yard Operator', 'Administrator'),
  ctrl.updateFlowStep
);

/**
 * @openapi
 * /api/yard/gate-out-requests/{id}/confirm-loaded:
 *   post:
 *     tags: [Yard Operations]
 *     summary: Xác nhận container đã cẩu lên xe, giải phóng slot và sẵn sàng xuất cổng
 *     security:
 *       - bearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema: { type: string, format: uuid }
 *     requestBody:
 *       required: true
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             required: [verifiedContainerId, verifiedSeal]
 *             properties:
 *               verifiedContainerId: { type: boolean }
 *               verifiedSeal: { type: boolean }
 *               verifiedNoDamage: { type: boolean }
 *               sealNumber: { type: string }
 *               containerCondition: { type: string }
 *               note: { type: string }
 *     responses:
 *       200:
 *         description: Xác nhận cẩu lên xe thành công, slot bãi được giải phóng
 *       400:
 *         description: Dữ liệu đối soát chưa đạt yêu cầu
 *       404:
 *         description: Không tìm thấy yêu cầu
 */
router.post(
  '/gate-out-requests/:id/confirm-loaded',
  authenticate,
  authorize('Yard Operator', 'Administrator'),
  ctrl.confirmLoadedOnTruck
);

// ─── THAO TÁC NGHIỆP VỤ CHUẨN BỊ BÃI (5A INBOUND & 5B OUTBOUND) ─────────────
router.post(
  '/gate-out-requests/:id/unstack',
  authenticate,
  authorize('Yard Operator', 'Administrator', 'Dispatcher'),
  ctrl.unstackMove
);

router.post(
  '/gate-out-requests/:id/buffer-stage',
  authenticate,
  authorize('Yard Operator', 'Administrator', 'Dispatcher'),
  ctrl.moveToBufferStage
);

router.post(
  '/gate-out-requests/:id/confirm-ready-delivery',
  authenticate,
  authorize('Yard Operator', 'Administrator', 'Dispatcher'),
  ctrl.confirmReadyDelivery
);

router.post(
  '/gate-out-requests/:id/confirm-ready-loading',
  authenticate,
  authorize('Yard Operator', 'Administrator', 'Dispatcher'),
  ctrl.confirmReadyLoading
);

router.post(
  '/gate-out-requests/:id/dispatch-apron',
  authenticate,
  authorize('Yard Operator', 'Administrator', 'Dispatcher'),
  ctrl.dispatchApronTruck
);

router.post(
  '/gate-out-requests/:id/reefer-power',
  authenticate,
  authorize('Yard Operator', 'Administrator', 'Dispatcher'),
  ctrl.toggleReeferPower
);

router.post(
  '/gate-out-requests/:id/shut-out',
  authenticate,
  authorize('Yard Operator', 'Administrator', 'Dispatcher'),
  ctrl.cancelPreparationOrShutOut
);

// ─── GATE-OUT DELIVERY & e-EIR WORKFLOW ENDPOINTS ────────────────────────────
const gateOutCtrl = require('./gate-out-delivery.controller');

/**
 * 1. Kiểm tra 5 điều kiện xuất container tại cổng vào theo thời gian thực
 * (e-D/O, thanh toán, khung giờ, xe/tài xế, container sẵn sàng bãi)
 */
router.post(
  '/gate-out/check-eligibility',
  authenticate,
  authorize('Gate Officer', 'Yard Operator', 'Dispatcher', 'Administrator', 'Driver', 'Carrier Staff', 'Transport Company'),
  gateOutCtrl.checkEligibility
);

/**
 * 2. Lập phiếu EIR điện tử (hướng ra) & xác nhận hoàn tất đối soát hiện trường
 */
router.post(
  '/gate-out/issue-eir',
  authenticate,
  authorize('Yard Operator', 'Gate Officer', 'Dispatcher', 'Administrator'),
  gateOutCtrl.issueGateOutEir
);

/**
 * 3. Ghi nhận xe ra cổng ra, kết thúc tính lưu bãi, phát ContainerGatedOut
 */
router.post(
  '/gate-out/confirm-exit',
  authenticate,
  authorize('Gate Officer', 'Administrator', 'Yard Operator', 'Dispatcher'),
  gateOutCtrl.confirmGateOutExit
);

/**
 * 4. Xử lý ngoại lệ trong quá trình xuất bãi (Sai cont, sai chì, EDoHeld, hư hỏng)
 */
router.post(
  '/gate-out/exception',
  authenticate,
  authorize('Yard Operator', 'Gate Officer', 'Dispatcher', 'Administrator'),
  gateOutCtrl.handleException
);

module.exports = router;

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

module.exports = router;

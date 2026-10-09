'use strict';

const { Router } = require('express');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const ctrl = require('./trips.controller');

const router = Router();

// ── Driver Routes (/api/driver/trips) ────────────────────────────────────────
// Chỉ Driver (và Admin để test) mới truy cập được

/**
 * @openapi
 * /api/driver/trips:
 *   get:
 *     tags: [Driver Trips]
 *     summary: Lấy danh sách chuyến vận chuyển của tài xế
 *     security:
 *       - bearerAuth: []
 *     parameters:
 *       - in: query
 *         name: status
 *         schema:
 *           type: string
 *           enum: [active, Assigned, Acknowledged, Vehicle Picked Up, Container Picked Up, Gate In, In Transit, Arrived at Yard, Container Delivered, Gate Out Completed, Vehicle Returned, Completed, Cancelled, all]
 *       - in: query
 *         name: page
 *         schema: { type: integer, default: 1 }
 *       - in: query
 *         name: limit
 *         schema: { type: integer, default: 20 }
 *     responses:
 *       200:
 *         description: Danh sách chuyến
 */
router.get(
  '/driver/trips',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.getMyTrips
);

/**
 * @openapi
 * /api/driver/trips/{id}:
 *   get:
 *     tags: [Driver Trips]
 *     summary: Xem chi tiết chuyến + lịch sử trạng thái
 *     security:
 *       - bearerAuth: []
 *     parameters:
 *       - in: path
 *         name: id
 *         required: true
 *         schema: { type: string, format: uuid }
 *     responses:
 *       200:
 *         description: Chi tiết chuyến
 *       404:
 *         description: Không tìm thấy
 */
router.get(
  '/driver/trips/:id',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.getTripById
);

/**
 * @openapi
 * /api/driver/trips/{id}/acknowledge:
 *   patch:
 *     tags: [Driver Trips]
 *     summary: "Bước 1: Tài xế xác nhận nhận chuyến (Assigned → Acknowledged)"
 *     security:
 *       - bearerAuth: []
 */
router.patch(
  '/driver/trips/:id/acknowledge',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.acknowledgeTrip
);

/**
 * @openapi
 * /api/driver/trips/{id}/pickup-vehicle:
 *   patch:
 *     tags: [Driver Trips]
 *     summary: "Bước 2: Xác nhận đã lấy xe (Acknowledged → Vehicle Picked Up)"
 *     security:
 *       - bearerAuth: []
 */
router.patch(
  '/driver/trips/:id/pickup-vehicle',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.pickupVehicle
);

/**
 * @openapi
 * /api/driver/trips/{id}/pickup-container:
 *   patch:
 *     tags: [Driver Trips]
 *     summary: "Bước 3: Xác nhận đã lấy container (Vehicle Picked Up → Container Picked Up)"
 *     security:
 *       - bearerAuth: []
 */
router.patch(
  '/driver/trips/:id/pickup-container',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.pickupContainer
);

/**
 * @openapi
 * /api/driver/trips/{id}/gate-in:
 *   patch:
 *     tags: [Driver Trips]
 *     summary: "Bước 4: Xác nhận Gate In (Container Picked Up → In Transit)"
 *     security:
 *       - bearerAuth: []
 */
router.patch(
  '/driver/trips/:id/gate-in',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.gateIn
);

/**
 * @openapi
 * /api/driver/trips/{id}/arrive-yard:
 *   patch:
 *     tags: [Driver Trips]
 *     summary: "Bước 5: Xác nhận đã đến Yard (In Transit → Arrived at Yard)"
 *     security:
 *       - bearerAuth: []
 */
router.patch(
  '/driver/trips/:id/arrive-yard',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.arriveAtYard
);

/**
 * @openapi
 * /api/driver/trips/{id}/deliver-container:
 *   patch:
 *     tags: [Driver Trips]
 *     summary: "Bước 6: Xác nhận giao container (Arrived at Yard → Container Delivered)"
 *     security:
 *       - bearerAuth: []
 */
router.patch(
  '/driver/trips/:id/deliver-container',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.deliverContainer
);

/**
 * @openapi
 * /api/driver/trips/{id}/gate-out:
 *   patch:
 *     tags: [Driver Trips]
 *     summary: "Bước 7: Xác nhận Gate Out (Container Delivered → Gate Out Completed)"
 *     security:
 *       - bearerAuth: []
 */
router.patch(
  '/driver/trips/:id/gate-out',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.gateOut
);

/**
 * @openapi
 * /api/driver/trips/{id}/return-vehicle:
 *   patch:
 *     tags: [Driver Trips]
 *     summary: "Bước 8: Xác nhận trả xe và hoàn thành chuyến (Gate Out Completed → Completed)"
 *     security:
 *       - bearerAuth: []
 */
router.patch(
  '/driver/trips/:id/return-vehicle',
  authenticate,
  authorize('Driver', 'Administrator'),
  ctrl.returnVehicle
);

// ── Admin / Dispatcher Routes (/api/trips) ────────────────────────────────────

/**
 * @openapi
 * /api/trips:
 *   get:
 *     tags: [Transport Trips Management]
 *     summary: "[Admin/Dispatcher] Lấy tất cả chuyến vận chuyển"
 *     security:
 *       - bearerAuth: []
 */
router.get(
  '/trips',
  authenticate,
  authorize('Administrator', 'Dispatcher'),
  ctrl.getAllTrips
);

/**
 * @openapi
 * /api/trips:
 *   post:
 *     tags: [Transport Trips Management]
 *     summary: "[Admin/Dispatcher] Tạo chuyến vận chuyển và giao cho Driver"
 *     security:
 *       - bearerAuth: []
 *     requestBody:
 *       required: true
 *       content:
 *         application/json:
 *           schema:
 *             type: object
 *             required: [driverId]
 *             properties:
 *               driverId: { type: string, format: uuid }
 *               bookingId: { type: string, format: uuid }
 *               truckPlate: { type: string }
 *               containerNo: { type: string }
 *               containerType: { type: string }
 *               gateInCode: { type: string }
 *               yardBlock: { type: string }
 *               yardSlot: { type: string }
 *               appointmentStart: { type: string, format: date-time }
 *               appointmentEnd: { type: string, format: date-time }
 *               note: { type: string }
 */
router.post(
  '/trips',
  authenticate,
  authorize('Administrator', 'Dispatcher'),
  ctrl.createTrip
);

/**
 * @openapi
 * /api/trips/{id}:
 *   get:
 *     tags: [Transport Trips Management]
 *     summary: "[Admin/Dispatcher] Xem chi tiết bất kỳ chuyến nào"
 *     security:
 *       - bearerAuth: []
 */
router.get(
  '/trips/:id',
  authenticate,
  authorize('Administrator', 'Dispatcher'),
  ctrl.getAnyTripById
);

module.exports = router;

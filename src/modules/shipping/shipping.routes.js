'use strict';

const { Router } = require('express');
const shippingService = require('./shipping.service');

const router = Router();

// Helper wrapper for async handlers
const asyncHandler = (fn) => (req, res, next) => {
  Promise.resolve(fn(req, res, next)).catch(next);
};


// ── Auth ─────────────────────────────────────────────────────────────────────
router.post(
  ['/v1/auth/login', '/auth/login'],
  asyncHandler(async (req, res) => {
    const { username } = req.body;
    res.json({
      success: true,
      data: {
        token: 'nexusport-shipping-jwt-token',
        user: {
          id: 'u-shipping-01',
          username: username || 'shipping01',
          fullName: 'Đại diện Hãng tàu Quốc tế (NexusPort)',
          role: 'Shipping Line',
          carrierCode: 'ONEV',
          carrierName: 'Ocean Network Express / Evergreen',
          email: 'shipping@nexusport.vn',
        },
      },
    });
  })
);

router.get(
  ['/v1/auth/me', '/auth/me'],
  asyncHandler(async (req, res) => {
    res.json({
      success: true,
      data: {
        id: 'u-shipping-01',
        username: 'shipping01',
        fullName: 'Đại diện Hãng tàu Quốc tế (NexusPort)',
        role: 'Shipping Line',
        carrierCode: 'ONEV',
        carrierName: 'Ocean Network Express / Evergreen',
        email: 'shipping@nexusport.vn',
      },
    });
  })
);

router.post(
  ['/v1/auth/logout', '/auth/logout'],
  asyncHandler(async (_req, res) => {
    res.json({ success: true, message: 'Đăng xuất thành công.' });
  })
);

// ── Overview / Stats ──────────────────────────────────────────────────────────
router.get(
  ['/v1/stats', '/stats'],
  asyncHandler(async (req, res) => {
    const stats = await shippingService.getStats();
    res.json(stats);
  })
);

// ── Bookings ────────────────────────────────────────────────────────────────
router.get(
  ['/v1/bookings', '/bookings'],
  asyncHandler(async (req, res) => {
    const result = await shippingService.getBookings(req.query);
    res.json(result);
  })
);

router.get(
  ['/v1/bookings/:id', '/bookings/:id'],
  asyncHandler(async (req, res) => {
    const booking = await shippingService.getBookingById(req.params.id);
    if (!booking) {
      return res.status(404).json({ success: false, message: 'Không tìm thấy booking.' });
    }
    res.json({ success: true, data: booking, ...booking });
  })
);

router.post(
  ['/v1/bookings/:id/approve', '/bookings/:id/approve'],
  asyncHandler(async (req, res) => {
    try {
      const updated = await shippingService.approveBooking(req.params.id, req.body);
      res.json({ success: true, data: updated, message: 'Duyệt booking thành công.' });
    } catch (err) {
      res.status(400).json({ success: false, message: err.message });
    }
  })
);

router.post(
  ['/v1/bookings/:id/reject', '/bookings/:id/reject'],
  asyncHandler(async (req, res) => {
    try {
      const updated = await shippingService.rejectBooking(req.params.id, req.body);
      res.json({ success: true, data: updated, message: 'Từ chối booking thành công.' });
    } catch (err) {
      res.status(400).json({ success: false, message: err.message });
    }
  })
);

router.post(
  ['/v1/bookings/:id/issue-ro', '/bookings/:id/issue-ro'],
  asyncHandler(async (req, res) => {
    try {
      const result = await shippingService.issueReleaseOrder(req.params.id, req.body);
      res.json({ success: true, data: result, message: 'Phát hành lệnh cấp vỏ thành công.' });
    } catch (err) {
      res.status(400).json({ success: false, message: err.message });
    }
  })
);

// ── Import Cargo & Manifest ──────────────────────────────────────────────────
router.get(
  ['/v1/voyages', '/voyages'],
  asyncHandler(async (_req, res) => {
    const result = await shippingService.getVoyages();
    res.json(result);
  })
);

router.get(
  ['/v1/voyages/:voyageNumber/manifest', '/voyages/:voyageNumber/manifest'],
  asyncHandler(async (req, res) => {
    const manifest = await shippingService.getManifestByVoyage(req.params.voyageNumber);
    res.json(manifest);
  })
);

router.get(
  ['/v1/bls/:blNumber', '/bls/:blNumber'],
  asyncHandler(async (req, res) => {
    const bl = await shippingService.getBLByNumber(req.params.blNumber);
    if (!bl) {
      return res.status(404).json({ success: false, message: 'Không tìm thấy vận đơn B/L.' });
    }
    res.json({ success: true, data: bl, ...bl });
  })
);

router.post(
  ['/v1/bls/:blNumber/issue-do', '/bls/:blNumber/issue-do'],
  asyncHandler(async (req, res) => {
    try {
      const result = await shippingService.issueDeliveryOrderForBL(req.params.blNumber, req.body);
      res.json(result);
    } catch (err) {
      res.status(400).json({ success: false, message: err.message });
    }
  })
);

router.get(
  ['/v1/bls/:blNumber/do', '/bls/:blNumber/do'],
  asyncHandler(async (req, res) => {
    const allDos = await shippingService.getDeliveryOrders({});
    const found = (allDos.items || []).find(
      (d) => d.blNumber.toLowerCase() === req.params.blNumber.toLowerCase()
    );
    if (!found) {
      return res.status(404).json({ success: false, message: 'Chưa cấp lệnh D/O cho vận đơn này.' });
    }
    res.json({ success: true, data: found, ...found });
  })
);

// ── Delivery Orders (eDO) ────────────────────────────────────────────────────
router.get(
  ['/v1/delivery-orders', '/delivery-orders'],
  asyncHandler(async (req, res) => {
    const result = await shippingService.getDeliveryOrders(req.query);
    res.json(result);
  })
);

router.post(
  ['/v1/delivery-orders/:id/extend', '/delivery-orders/:id/extend'],
  asyncHandler(async (req, res) => {
    try {
      const updated = await shippingService.extendDeliveryOrder(req.params.id, req.body.newValidUntil);
      res.json({ success: true, data: updated, message: 'Gia hạn lệnh D/O thành công.' });
    } catch (err) {
      res.status(400).json({ success: false, message: err.message });
    }
  })
);

router.post(
  ['/v1/delivery-orders/:id/hold', '/delivery-orders/:id/hold'],
  asyncHandler(async (req, res) => {
    try {
      const updated = await shippingService.holdDeliveryOrder(req.params.id, req.body.reason);
      res.json({ success: true, data: updated, message: 'Đã giữ lệnh D/O.' });
    } catch (err) {
      res.status(400).json({ success: false, message: err.message });
    }
  })
);

router.post(
  ['/v1/delivery-orders/:id/release', '/delivery-orders/:id/release'],
  asyncHandler(async (req, res) => {
    try {
      const updated = await shippingService.releaseDeliveryOrder(req.params.id);
      res.json({ success: true, data: updated, message: 'Đã nhả lệnh D/O.' });
    } catch (err) {
      res.status(400).json({ success: false, message: err.message });
    }
  })
);

router.post(
  ['/v1/delivery-orders/:id/cancel', '/delivery-orders/:id/cancel'],
  asyncHandler(async (req, res) => {
    try {
      const updated = await shippingService.cancelDeliveryOrder(req.params.id, req.body.reason);
      res.json({ success: true, data: updated, message: 'Đã hủy lệnh D/O.' });
    } catch (err) {
      res.status(400).json({ success: false, message: err.message });
    }
  })
);

// ── Containers & Depots ──────────────────────────────────────────────────────
router.get(
  ['/v1/containers', '/containers'],
  asyncHandler(async (req, res) => {
    const result = await shippingService.getContainers(req.query);
    res.json(result);
  })
);

router.get(
  ['/v1/containers/:containerNumber', '/containers/:containerNumber'],
  asyncHandler(async (req, res) => {
    const cnt = await shippingService.getContainerByNumber(req.params.containerNumber);
    if (!cnt) {
      return res.status(404).json({ success: false, message: 'Không tìm thấy container.' });
    }
    res.json({ success: true, data: cnt, ...cnt });
  })
);

// ── EDI / Port Sync ──────────────────────────────────────────────────────────
router.get(
  ['/v1/port-sync/messages', '/port-sync/messages'],
  asyncHandler(async (_req, res) => {
    const result = await shippingService.getPortMessages();
    res.json(result);
  })
);

router.post(
  ['/v1/port-sync/retry/:id', '/port-sync/retry/:id'],
  asyncHandler(async (req, res) => {
    const result = await shippingService.retryPortMessage(req.params.id);
    res.json(result);
  })
);

module.exports = router;

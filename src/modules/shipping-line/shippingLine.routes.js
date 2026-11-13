'use strict';

const { Router } = require('express');
const { ROLES } = require('../../config/rbac');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const shippingLineService = require('./shippingLine.service');

const router = Router();
const manifestWriter = [authenticate, authorize(ROLES.SHIPPING_LINE)];

// Wrapper async handler
const asyncHandler = (fn) => (req, res, next) => {
  Promise.resolve(fn(req, res, next)).catch(next);
};

// 0. Seed / Reset dữ liệu mẫu
router.post('/seed/reset', ...manifestWriter, asyncHandler(async (_req, res) => {
  const data = await shippingLineService.resetSeedData();
  res.json({
    success: true,
    message: 'Khôi phục toàn bộ dữ liệu mẫu nghiệp vụ Hãng tàu thành công!',
    data,
  });
}));

// 1. Dashboard
router.get('/dashboard', asyncHandler(async (_req, res) => {
  const data = await shippingLineService.getDashboardSummary();
  res.json({ success: true, data });
}));

// 2. Vessels & Voyages
router.get('/vessels', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getVessels(req.query.search);
  res.json({ success: true, data });
}));

router.get('/voyages', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getVoyages(req.query);
  res.json({ success: true, data });
}));

router.get('/voyages/:id', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getVoyageDetail(req.params.id);
  res.json({ success: true, data });
}));

router.get('/voyages/:voyageNumber/candidate-bls', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getCandidateBlsForVoyage(req.params.voyageNumber);
  res.json({ success: true, data });
}));

router.post('/voyages', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const data = await shippingLineService.createVoyage(req.body, user);
  res.status(201).json({ success: true, message: 'Tạo chuyến tàu thành công', data });
}));

router.put('/voyages/:id', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const data = await shippingLineService.updateVoyage(req.params.id, req.body, user);
  res.json({ success: true, message: 'Cập nhật chuyến tàu thành công', data });
}));

// 3. Manifests
router.get('/manifests', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getManifests(req.query);
  res.json({ success: true, data });
}));

router.get('/manifests/:id', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getManifestDetail(req.params.id);
  res.json({ success: true, data });
}));

router.post('/manifests', ...manifestWriter, asyncHandler(async (req, res) => {
  const user = req.user.username;
  const data = await shippingLineService.createManifest(req.body, user);
  res.status(201).json({ success: true, message: 'Lưu bản khai Manifest thành công', data });
}));

router.post('/manifests/:id/resolve-exception', ...manifestWriter, asyncHandler(async (req, res) => {
  const user = req.user.username;
  const { recordId, ...updatedData } = req.body;
  const data = await shippingLineService.resolveManifestException(req.params.id, recordId, updatedData, user);
  res.json({ success: true, message: 'Xử lý ngoại lệ container thành công', data });
}));

router.post('/manifests/:id/reconcile', ...manifestWriter, asyncHandler(async (req, res) => {
  const data = await shippingLineService.reconcileManifest(req.params.id);
  res.json({ success: true, message: 'Đối soát Manifest với dữ liệu cảng thành công', data });
}));

router.post('/manifests/:id/send-to-port', ...manifestWriter, asyncHandler(async (req, res) => {
  const data = await shippingLineService.sendManifestToPort(req.params.id);
  res.json({ success: true, message: 'Đã gửi dữ liệu Bản khai hàng hóa cho Cảng Tiên Sa thành công', data });
}));

router.get('/manifests/port/synced', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getSyncedManifests();
  res.json({ success: true, data });
}));

router.get('/manifests/port/lookup/:containerNo', asyncHandler(async (req, res) => {
  const data = await shippingLineService.lookupContainerManifest(req.params.containerNo);
  res.json({ success: true, data });
}));

// 4. Bills of Lading (B/L) & Shipping Instructions (SI)
router.get('/shipping-instructions', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getShippingInstructions(req.query);
  res.json({ success: true, data });
}));

router.get('/bills-of-lading', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getBillsOfLading(req.query);
  res.json({ success: true, data });
}));

router.get('/bills-of-lading/:id', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getBillOfLadingDetail(req.params.id);
  res.json({ success: true, data });
}));

router.post('/bills-of-lading', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const data = await shippingLineService.createBillOfLading(req.body, user);
  res.status(201).json({ success: true, message: 'Tạo Vận đơn B/L thành công', data });
}));

router.patch('/bills-of-lading/:id/status', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const data = await shippingLineService.updateBlStatus(req.params.id, req.body.status, user);
  res.json({ success: true, message: 'Cập nhật trạng thái vận đơn thành công', data });
}));

router.post('/bills-of-lading/:id/collect-fees', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const data = await shippingLineService.collectBlFees(req.params.id, user);
  res.json({ success: true, message: 'Ghi nhận thu phí vận đơn thành công', data });
}));

// 5. Delivery Orders (eDO)
router.get('/delivery-orders', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getDeliveryOrders(req.query);
  res.json({ success: true, data });
}));

router.get('/delivery-orders/eligibility/:blNumber', asyncHandler(async (req, res) => {
  const data = await shippingLineService.checkEdoEligibility(req.params.blNumber);
  res.json({ success: true, data });
}));

router.post('/delivery-orders/collect-fees', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const { blNumber } = req.body;
  if (!blNumber) throw new Error('Số B/L là bắt buộc');
  const data = await shippingLineService.collectBlFees(blNumber, user);
  res.json({ success: true, message: 'Ghi nhận thu phí vận đơn thành công', data });
}));

router.post('/delivery-orders/surrender-bl', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const { blNumber } = req.body;
  if (!blNumber) throw new Error('Số B/L là bắt buộc');
  const data = await shippingLineService.surrenderBl(blNumber, user);
  res.json({ success: true, message: 'Thu hồi B/L gốc thành công', data });
}));

router.get('/delivery-orders/:id', asyncHandler(async (req, res) => {
  const data = await shippingLineService.getDeliveryOrderDetail(req.params.id);
  res.json({ success: true, data });
}));

router.post('/delivery-orders', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const data = await shippingLineService.issueEdo(req.body, user);
  res.status(201).json({ success: true, message: 'Phát hành lệnh giao hàng điện tử (eDO) thành công', data });
}));

router.post('/delivery-orders/issue', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const data = await shippingLineService.issueEdo(req.body, user);
  res.status(201).json({ success: true, message: 'Phát hành lệnh giao hàng điện tử (eDO) thành công', data });
}));

router.post('/delivery-orders/:id/extend', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const data = await shippingLineService.extendEdo(req.params.id, req.body.validUntil, req.body.reason, user);
  res.json({ success: true, message: 'Gia hạn lệnh giao hàng eDO thành công', data });
}));

router.post('/delivery-orders/:id/cancel', asyncHandler(async (req, res) => {
  const user = req.user ? req.user.username : 'shipping01';
  const data = await shippingLineService.cancelEdo(req.params.id, req.body.reason, user);
  res.json({ success: true, message: 'Hủy lệnh giao hàng eDO thành công', data });
}));

// 6. Gate Out Port Verification
router.get('/gate-out/verify/:containerNumber', asyncHandler(async (req, res) => {
  const data = await shippingLineService.verifyEdoForGateOut(req.params.containerNumber);
  res.json({ success: true, data });
}));

module.exports = router;


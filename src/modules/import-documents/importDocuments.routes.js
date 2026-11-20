'use strict';

const { Router } = require('express');
const { ROLES } = require('../../config/rbac');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const service = require('./importDocuments.service');

const router = Router();
const asyncHandler = (fn) => (req, res, next) => Promise.resolve(fn(req, res, next)).catch(next);
const ok = (res, data) => res.json({ success: true, data });

const post = require('./postIssuance.service');

// Mỗi trạng thái chỉ một vai trò được chuyển hồ sơ sang bước tiếp theo; vai trò còn lại chỉ được xem.
const COMPANY = ['Transport Company', 'Carrier Staff'];
const readers = [authenticate, authorize(ROLES.SHIPPING_LINE, ROLES.ADMINISTRATOR, ...COMPANY)];
const line = [authenticate, authorize(ROLES.SHIPPING_LINE)];
const company = [authenticate, authorize(...COMPANY)];

router.get('/pre-alerts', ...line, asyncHandler(async (_req, res) => ok(res, await service.listPreAlerts())));
router.post('/pre-alerts/:id/receive', ...line, asyncHandler(async (req, res) => ok(res, await service.receivePreAlert(req.params.id, req.user))));
router.get('/companies', ...line, asyncHandler(async (_req, res) => ok(res, await service.listCompanies())));
router.post('/deadlines/process', ...line, asyncHandler(async (_req, res) => ok(res, { accepted: await service.autoAcceptOverdue() })));

// Ngoại lệ sau phát hành B/L (đặt trước '/:id' để không bị nuốt)
router.post('/requests/:requestId/resolve', ...line, asyncHandler(async (req, res) => ok(res, await post.resolve(req.params.requestId, req.body, req.user))));
router.post('/requests/:requestId/cancel', ...company, asyncHandler(async (req, res) => ok(res, await post.cancel(req.params.requestId, req.user))));
router.post('/requests/:requestId/original-returned', ...line, asyncHandler(async (req, res) => ok(res, await post.confirmOriginalReturned(req.params.requestId, req.user))));
router.get('/:id/requests', ...readers, asyncHandler(async (req, res) => ok(res, await post.listForFile(req.params.id, req.user))));
router.post('/:id/requests', ...company, asyncHandler(async (req, res) => ok(res, await post.create(req.params.id, req.body, req.user))));
router.post('/:id/schedule-change', ...line, asyncHandler(async (req, res) => ok(res, await post.changeSchedule(req.params.id, req.body, req.user))));

router.get('/', ...readers, asyncHandler(async (req, res) => ok(res, await service.list(req.user, { status: req.query.status, search: req.query.search }))));
router.get('/:id', ...readers, asyncHandler(async (req, res) => ok(res, await service.get(req.params.id, req.user))));

// Hãng tàu (đại lý đầu nhập)
router.post('/:id/assign-consignee', ...line, asyncHandler(async (req, res) => ok(res, await service.assignConsignee(req.params.id, req.body.carrierId, req.user))));
router.post('/:id/reconcile', ...line, asyncHandler(async (req, res) => ok(res, await service.reconcile(req.params.id, req.body, req.user))));
router.post('/:id/resolve-mismatch', ...line, asyncHandler(async (req, res) => ok(res, await service.resolveMismatch(req.params.id, req.body, req.user))));
router.post('/:id/arrival-notice', ...line, asyncHandler(async (req, res) => ok(res, await service.sendArrivalNotice(req.params.id, req.body, req.user))));
router.post('/:id/resolve-amendment', ...line, asyncHandler(async (req, res) => ok(res, await service.resolveAmendment(req.params.id, req.body, req.user))));
router.post('/:id/collect-fees', ...line, asyncHandler(async (req, res) => ok(res, await service.collectFees(req.params.id, req.user))));
router.post('/:id/confirm-original', ...line, asyncHandler(async (req, res) => ok(res, await service.confirmOriginalReceived(req.params.id, req.user))));
router.post('/:id/verify-release', ...line, asyncHandler(async (req, res) => ok(res, await service.verifyRelease(req.params.id, req.user))));
router.post('/:id/issue-do', ...line, asyncHandler(async (req, res) => ok(res, await service.issueDo(req.params.id, req.body, req.user))));

// Transport company (phía consignee)
router.post('/:id/confirm-notice', ...company, asyncHandler(async (req, res) => ok(res, await service.confirmNotice(req.params.id, req.body, req.user))));
router.post('/:id/request-amendment', ...company, asyncHandler(async (req, res) => ok(res, await service.requestAmendment(req.params.id, req.body, req.user))));
router.post('/:id/authorization', ...company, asyncHandler(async (req, res) => ok(res, await service.setAuthorization(req.params.id, req.body.url, req.user))));
router.post('/:id/request-do', ...company, asyncHandler(async (req, res) => ok(res, await service.requestDo(req.params.id, req.user))));

module.exports = router;

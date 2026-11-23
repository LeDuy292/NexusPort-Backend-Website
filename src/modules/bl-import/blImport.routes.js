'use strict';

const express = require('express');
const { ROLES } = require('../../config/rbac');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const service = require('./blImport.service');
const reconcile = require('./blReconcile.service');

const COMPANY = ['Transport Company', 'Carrier Staff'];
const company = [authenticate, authorize(...COMPANY)];

const router = express.Router();
const asyncHandler = (fn) => (req, res, next) => Promise.resolve(fn(req, res, next)).catch(next);
const ok = (res, data) => res.json({ success: true, data });
const line = [authenticate, authorize(ROLES.SHIPPING_LINE)];


router.get('/manifests/:manifestId/bills', ...line, asyncHandler(async (req, res) => ok(res, await service.listBills(req.params.manifestId, req.user))));
router.post('/manifests/:manifestId/bills/:blNumber/issue', ...line, asyncHandler(async (req, res) => ok(res, await service.issue(req.params.manifestId, req.params.blNumber, req.body || {}, req.user))));
router.post('/manifests/:manifestId/bills/:blNumber/sync-containers', ...line, asyncHandler(async (req, res) => ok(res, await service.syncContainers(req.params.manifestId, req.params.blNumber, req.user))));
router.get('/bills', ...line, asyncHandler(async (_req, res) => ok(res, await reconcile.listBills())));
router.get('/bills/:blNumber/reconcile', ...line, asyncHandler(async (req, res) => ok(res, await reconcile.reconcileBill(req.params.blNumber))));

// Transport company: vận đơn của công ty mình (chủ hàng xuất hoặc người nhận hàng nhập)
router.get('/my-bills', ...company, asyncHandler(async (req, res) => ok(res, await reconcile.myBills(req.user))));
router.get('/my-bills/:blNumber', ...company, asyncHandler(async (req, res) => ok(res, await reconcile.myBill(req.user, req.params.blNumber))));


module.exports = router;

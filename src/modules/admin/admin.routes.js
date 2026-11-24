'use strict';

const { Router } = require('express');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const service = require('./admin.service');

const router = Router();
const handler = (fn, status = 200) => async (req, res, next) => {
  try { res.status(status).json({ success: true, data: await fn(req) }); }
  catch (error) {
    if (error.statusCode) return res.status(error.statusCode).json({ success: false, message: error.message });
    return next(error);
  }
};

router.use(authenticate, authorize('Administrator'));
router.get('/overview', handler(() => service.getOverview()));
router.get('/reports', handler((req) => service.getReports(req.query.days)));
router.get('/billing', handler((req) => service.getBilling(req.query)));
router.get('/gates', handler(() => service.listGates()));
router.post('/gates', handler((req) => service.createGate(req.body), 201));
router.patch('/gates/:id', handler((req) => service.updateGate(req.params.id, req.body)));
router.post('/shipping-line-accounts', handler((req) => service.createShippingLineAccount(req.body), 201));
const actor = (req) => ({ id: req.user && req.user.id });
router.get('/equipment', handler(() => service.listEquipment()));
router.post('/equipment', handler((req) => service.createEquipment(actor(req), req.body), 201));
router.patch('/equipment/:id', handler((req) => service.updateEquipment(actor(req), req.params.id, req.body)));
router.delete('/equipment/:id', handler((req) => service.deleteEquipment(actor(req), req.params.id)));

module.exports = router;

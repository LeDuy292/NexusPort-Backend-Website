'use strict';

const { Router } = require('express');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const service = require('./driver-bookings.service');
const { getAssignmentOptions } = require('../users/driver-assignment.service');

const router = Router();
const isUUID = (value) => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
router.use(authenticate);
const driver = authorize('Driver');
const admin = authorize('Administrator');
const handle = (fn) => async (req, res, next) => {
  try { await fn(req, res); }
  catch (error) {
    if (error.statusCode) res.status(error.statusCode).json({ success: false, message: error.message });
    else next(error);
  }
};
const uuidParam = (param) => (req, res, next) => {
  if (!isUUID(req.params[param] || '')) return res.status(422).json({ success: false, message: 'Mã định danh không hợp lệ.' });
  next();
};

router.get('/feed', driver, handle(async (req, res) => {
  res.json({ success: true, data: await service.getFeed(req.user.id) });
}));
router.get('/assignment-options', admin, handle(async (_req, res) => {
  res.json({ success: true, data: await getAssignmentOptions() });
}));
router.post('/notifications/:id/read', driver, uuidParam('id'), handle(async (req, res) => {
  await service.markRead(req.user.id, req.params.id);
  res.json({ success: true });
}));
router.get('/:id', driver, uuidParam('id'), handle(async (req, res) => {
  res.json({ success: true, data: { booking: await service.getBooking(req.user.id, req.params.id) } });
}));

module.exports = router;

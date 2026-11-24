'use strict';

const { Router } = require('express');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const service = require('./dispatcher.service');

const router = Router();

router.use(authenticate, authorize('Dispatcher', 'Administrator'));
router.get('/overview', async (req, res, next) => {
  try { res.json({ success: true, data: await service.getOverview() }); } catch (error) { next(error); }
});

module.exports = router;

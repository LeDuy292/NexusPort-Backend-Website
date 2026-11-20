'use strict';
const { Router } = require('express');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const service = require('./dispatch.service');
const board = require('./dispatch-board');
const { pumpDispatchEvents } = require('./dispatch.realtime');
const router = Router();
const boardReader = authorize('Dispatcher', 'Administrator', 'Transport Company', 'Carrier Staff');
const canSeeYardTrips = req => ['Dispatcher', 'Administrator'].includes(req.user.role);
const handler = fn => async (req,res,next) => {
  try { res.json({ success: true, data: await fn(req) }); }
  catch(error) { if(error.statusCode) res.status(error.statusCode).json({success:false,message:error.message}); else next(error); }
};
router.use(authenticate);
router.get('/board', boardReader, handler(req => board.getBoard(req.query.date, req.query, canSeeYardTrips(req))));
router.get('/board/booking', boardReader, handler(req => board.getBoardBooking(req.query, canSeeYardTrips(req))));
router.post('/:id/:action', authorize('Driver','Dispatcher','Administrator'), handler(async req => {
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(req.params.id)) {
    throw Object.assign(new Error('Mã booking không hợp lệ.'), {statusCode:422});
  }
  const allowed = req.user.role==='Driver' ? ['arrive','ready','not-ready','acknowledge'] : ['call','delay','release'];
  if (!allowed.includes(req.params.action)) throw Object.assign(new Error('Thao tác không được phép.'), {statusCode:403});
  const booking = await service.act(req.user.id,req.user.role,req.params.id,req.params.action,req.body);
  void pumpDispatchEvents();
  return { booking };
}));
module.exports = router;

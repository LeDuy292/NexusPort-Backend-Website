'use strict';

const tripsService = require('./trips.service');

function handleError(err, res, next) {
  if (err.statusCode) {
    return res.status(err.statusCode).json({ success: false, message: err.message });
  }
  next(err);
}

/**
 * GET /api/driver/trips
 * Tài xế lấy danh sách chuyến vận chuyển của mình.
 */
async function getMyTrips(req, res, next) {
  try {
    const driverId = req.user.id;
    const { status, page, limit } = req.query;
    const result = await tripsService.getMyTrips(driverId, { status, page, limit });
    return res.status(200).json({ success: true, data: result });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * GET /api/driver/trips/:id
 * Tài xế xem chi tiết 1 chuyến + lịch sử trạng thái.
 */
async function getTripById(req, res, next) {
  try {
    const trip = await tripsService.getTripById(req.params.id, req.user.id);
    return res.status(200).json({ success: true, data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * PATCH /api/driver/trips/:id/acknowledge
 * Tài xế xác nhận nhận chuyến (Assigned → Acknowledged).
 */
async function acknowledgeTrip(req, res, next) {
  try {
    const trip = await tripsService.acknowledgeTrip(req.params.id, req.user.id);
    return res.status(200).json({ success: true, message: 'Đã xác nhận nhận chuyến.', data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * PATCH /api/driver/trips/:id/pickup-vehicle
 * Tài xế xác nhận đã lấy xe (Acknowledged → Vehicle Picked Up).
 */
async function pickupVehicle(req, res, next) {
  try {
    const trip = await tripsService.confirmVehiclePickup(req.params.id, req.user.id, req.body.note);
    return res.status(200).json({ success: true, message: 'Đã xác nhận lấy xe.', data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * PATCH /api/driver/trips/:id/pickup-container
 * Tài xế xác nhận đã lấy container (Vehicle Picked Up → Container Picked Up).
 */
async function pickupContainer(req, res, next) {
  try {
    const trip = await tripsService.confirmContainerPickup(req.params.id, req.user.id, req.body.note);
    return res.status(200).json({ success: true, message: 'Đã xác nhận lấy container.', data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * PATCH /api/driver/trips/:id/gate-in
 * Tài xế xác nhận Gate In (Container Picked Up → In Transit).
 */
async function gateIn(req, res, next) {
  try {
    const trip = await tripsService.confirmGateIn(req.params.id, req.user.id, req.body.note);
    return res.status(200).json({ success: true, message: 'Đã Gate In thành công. Bắt đầu vận chuyển.', data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * PATCH /api/driver/trips/:id/arrive-yard
 * Tài xế xác nhận đã đến Yard (In Transit → Arrived at Yard).
 */
async function arriveAtYard(req, res, next) {
  try {
    const trip = await tripsService.confirmArrivedAtYard(req.params.id, req.user.id, req.body.note);
    return res.status(200).json({ success: true, message: 'Đã xác nhận đến Yard.', data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * PATCH /api/driver/trips/:id/deliver-container
 * Tài xế xác nhận giao container (Arrived at Yard → Container Delivered).
 */
async function deliverContainer(req, res, next) {
  try {
    const trip = await tripsService.confirmContainerDelivery(req.params.id, req.user.id, req.body);
    return res.status(200).json({ success: true, message: 'Đã xác nhận giao container.', data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * PATCH /api/driver/trips/:id/gate-out
 * Tài xế xác nhận Gate Out (Container Delivered → Gate Out Completed).
 */
async function gateOut(req, res, next) {
  try {
    const trip = await tripsService.confirmGateOut(req.params.id, req.user.id, req.body.note);
    return res.status(200).json({ success: true, message: 'Đã Gate Out thành công.', data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * PATCH /api/driver/trips/:id/return-vehicle
 * Tài xế xác nhận trả xe và hoàn thành chuyến (Gate Out Completed → Completed).
 */
async function returnVehicle(req, res, next) {
  try {
    const trip = await tripsService.confirmVehicleReturn(req.params.id, req.user.id, req.body.note);
    return res.status(200).json({ success: true, message: 'Chuyến đã hoàn thành. Cảm ơn bạn!', data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

// ── Admin/Dispatcher endpoints ────────────────────────────────────────────────

/**
 * GET /api/trips (Admin/Dispatcher)
 * Lấy tất cả chuyến, có thể lọc theo driver, status.
 */
async function getAllTrips(req, res, next) {
  try {
    const { driverId, status, page, limit } = req.query;
    const result = await tripsService.getAllTrips({ driverId, status, page, limit });
    return res.status(200).json({ success: true, data: result });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * POST /api/trips (Admin/Dispatcher)
 * Tạo chuyến vận chuyển mới và giao cho Driver.
 */
async function createTrip(req, res, next) {
  try {
    const {
      driverId, bookingId, bookingCode, bookingType, truckPlate, truckId,
      containerNo, containerType, cargoType,
      gateInCode, gateOutCode, yardBlock, yardSlot, yardZone, yardInstructions,
      sealNo, containerCondition, recipientName,
      appointmentStart, appointmentEnd, note,
    } = req.body;

    if (!driverId) {
      return res.status(422).json({ success: false, message: 'driverId là bắt buộc.' });
    }

    const trip = await tripsService.createTrip({
      driverId, bookingId, bookingCode, bookingType, truckPlate, truckId,
      containerNo, containerType, cargoType,
      gateInCode, gateOutCode, yardBlock, yardSlot, yardZone, yardInstructions,
      sealNo, containerCondition, recipientName,
      appointmentStart, appointmentEnd, note,
      assignedBy: req.user.id,
    });

    return res.status(201).json({ success: true, message: 'Tạo chuyến vận chuyển thành công.', data: { trip } });
  } catch (err) {
    handleError(err, res, next);
  }
}

/**
 * GET /api/trips/:id (Admin/Dispatcher – xem bất kỳ chuyến nào)
 */
async function getAnyTripById(req, res, next) {
  try {
    const { TransportTrip, TransportTripHistory } = require('../../models/TransportTrip');
    const { NEXT_STATUS_MAP } = require('../../models/TransportTrip');
    const trip = await TransportTrip.findByPk(req.params.id, {
      include: [{ model: TransportTripHistory, as: 'history', order: [['changed_at', 'ASC']] }],
    });
    if (!trip) {
      return res.status(404).json({ success: false, message: 'Không tìm thấy chuyến.' });
    }
    return res.status(200).json({ success: true, data: { trip: { ...trip.toJSON(), nextStep: NEXT_STATUS_MAP[trip.status] || null } } });
  } catch (err) {
    handleError(err, res, next);
  }
}

module.exports = {
  getMyTrips,
  getTripById,
  acknowledgeTrip,
  pickupVehicle,
  pickupContainer,
  gateIn,
  arriveAtYard,
  deliverContainer,
  gateOut,
  returnVehicle,
  getAllTrips,
  createTrip,
  getAnyTripById,
};

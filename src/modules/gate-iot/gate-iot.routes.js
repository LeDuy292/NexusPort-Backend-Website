'use strict';

const { Router } = require('express');
const { timingSafeEqual } = require('node:crypto');
const { getIO } = require('../../config/socket');
const { sequelize } = require('../../config/database');
const { QueryTypes } = require('sequelize');
const { authenticate } = require('../../middlewares/authenticate');
const { authorize } = require('../../middlewares/authorize');
const { authorizeEntry, claimInboundHardwareOpen, entryGate, normalizePlate } = require('./gate-entry-admission');

const router = Router();
const gateOfficer = authorize('Gate Officer', 'Gate', 'Administrator', 'Admin');

router.post('/entry-admission', authenticate, gateOfficer, async (req, res, next) => {
  try {
    const data = await authorizeEntry(req.user.id, req.body ?? {});
    res.json({ success: true, data });
  } catch (error) {
    if (error.statusCode) return res.status(error.statusCode).json({ success: false, message: error.message });
    next(error);
  }
});

// The random command ID is issued only after the Gate backend accepts the verification.
router.post('/barrier-commands/:id/claim-hardware', async (req, res, next) => {
  if (!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(req.params.id)) return res.sendStatus(404);
  try {
    const accepted = await claimInboundHardwareOpen(req.params.id);
    res.status(accepted ? 200 : 409).json({ accepted });
  } catch (error) {
    next(error);
  }
});

/**
 * GET /api/gate/verify-plate?plate=47AB-294.69
 * Tra cứu chỉ đọc. Làn vào kiểm tra lệnh đang có hiệu lực. Làn ra giữ tra cứu booking cho thiết bị cũ.
 */
router.get('/verify-plate', async (req, res) => {
  try {
    const rawPlate = String(req.query.plate || '').trim();
    if (!rawPlate) {
      return res.json({ valid: false, reason: 'missing_plate' });
    }

    // Normalize: "47-AB 294.69" / "47AB29469" / "47AB-294.69" → "47AB29469"
    const normalized = normalizePlate(rawPlate);
    const gateCode = String(req.query.gateCode || 'GATE-01').toUpperCase();
    if (entryGate(gateCode)) {
      const orders = await sequelize.query(`SELECT e.id FROM gate_entry_orders e JOIN bookings b ON e.booking_id=b.id
        WHERE e.vehicle_plate=:normalized AND e.status='active' AND now()>=e.valid_from AND now()<e.valid_until
          AND e.driver_id=b.driver_id AND e.truck_id=b.truck_id AND b.status::text IN ('approved','ready')
        LIMIT 2`, { replacements: { normalized }, type: QueryTypes.SELECT });
      return res.json({ valid: orders.length === 1 });
    }
    if (!['GATE-02', 'GATE-03', 'GATE_OUT', 'GATE_OUT_A'].includes(gateCode)) {
      return res.status(422).json({ valid: false, reason: 'invalid_gate' });
    }

    const result = await sequelize.query(
      `SELECT id, booking_code, vehicle_plate, status, appointment_start, appointment_end, driver_name, gate_type
       FROM bookings
       WHERE status IN ('approved', 'ready', 'checked_in')
         AND appointment_start <= now()
         AND appointment_end   >= now()
         AND UPPER(REPLACE(REPLACE(REPLACE(vehicle_plate, '-', ''), '.', ''), ' ', '')) = :normalized
       ORDER BY appointment_start ASC
       LIMIT 1`,
      { replacements: { normalized }, type: QueryTypes.SELECT }
    );

    if (!result || result.length === 0) {
      console.log(`[verify-plate] '${rawPlate}' (normalized: '${normalized}') → NOT FOUND`);
      return res.json({ valid: false, reason: 'no_active_booking', plate: rawPlate });
    }

    const booking = result[0];
    console.log(`[verify-plate] '${rawPlate}' → FOUND booking ${booking.booking_code} (${booking.status})`);
    return res.json({
      valid: true,
      plate: rawPlate,
      booking: {
        id: booking.id,
        bookingCode: booking.booking_code,
        vehiclePlate: booking.vehicle_plate,
        status: booking.status,
        driverName: booking.driver_name,
        gateType: booking.gate_type,
        appointmentStart: booking.appointment_start,
        appointmentEnd: booking.appointment_end,
      }
    });
  } catch (err) {
    console.error('[verify-plate] DB error:', err.message);
    return res.status(500).json({ valid: false, reason: 'server_error', detail: err.message });
  }
});

/**
 * POST /api/gate/iot-event
 * Tiếp nhận sự kiện quét biển số từ thiết bị IoT / AI Service
 * và phát Realtime qua Socket.IO tới màn hình Nhân viên Cổng (Gate Officer)
 */
function registeredCamera(req) {
  const secret = Buffer.from(process.env.GATE_IOT_SHARED_KEY || '');
  const supplied = Buffer.from(req.get('X-Gate-IoT-Key') || '');
  return secret.length > 0 && secret.length === supplied.length && timingSafeEqual(secret, supplied);
}

router.post('/iot-event', (req, res) => {
  const io = getIO();
  const eventData = req.body || {};

  const detectedPlate = (eventData.detectedPlate || '').trim();

  const payload = {
    gateCode: eventData.gateCode || 'GATE_IN_A',
    laneCode: eventData.laneCode || (eventData.lane === 'OUT' ? 'LANE_02' : 'LANE_01'),
    lane: eventData.lane || (eventData.laneCode === 'LANE_02' ? 'OUT' : 'IN'),
    cameraId: eventData.cameraId || 'CAM_GATE_01',
    detectedPlate: detectedPlate || 'KHÔNG_NHẬN_DIỆN',
    plateConfidence: eventData.plateConfidence || 0.0,
    vehicleDetected: eventData.vehicleDetected !== false,
    vehicleType: eventData.vehicleType || 'container_truck',
    vehiclePlateImageUrl: eventData.vehiclePlateImageUrl || null,
    overviewImageUrl: eventData.overviewImageUrl || null,
    timestamp: eventData.timestamp || new Date().toISOString(),
    verificationType: eventData.verificationType || (eventData.lane === 'OUT' ? 'AI_GATE_OUT' : 'AI_GATE_IN'),
    trustedCamera: registeredCamera(req),
    source: eventData.source || 'IoT_Camera',
    decision: eventData.decision || (detectedPlate ? 'SUCCESS' : 'FAIL'),
  };

  if (io) {
    io.to('gate:all').emit('gate:iot_scan', payload);
  }

  return res.status(200).json({
    success: true,
    message: 'Đã nhận sự kiện IoT và phát Realtime tới Cổng kiểm soát.',
    data: payload,
  });
});

/**
 * POST /api/gate/barrier-status
 * Tiếp nhận cập nhật trạng thái Barie từ ESP32 / AI Service (ví dụ sau 30s tự hạ)
 * và phát Realtime qua Socket.IO tới màn hình Cổng kiểm soát (Gate Control UI).
 */
router.post('/barrier-status', (req, res) => {
  const io = getIO();
  const { lane = 'IN', state = 'closed', gateCode } = req.body || {};

  const normLane = String(lane).toUpperCase() === 'OUT' ? 'OUT' : 'IN';
  const normState = String(state).toLowerCase() === 'opened' ? 'opened' : 'closed';
  const mappedGateCode = gateCode || (normLane === 'OUT' ? 'GATE_OUT' : 'GATE_IN');

  const payload = {
    lane: normLane,
    state: normState,
    gateCode: mappedGateCode,
    timestamp: req.body?.timestamp || new Date().toISOString(),
    trustedCamera: registeredCamera(req),
  };

  if (io) {
    io.to('gate:all').emit('gate:barrier_state_changed', payload);
  }

  return res.status(200).json({
    success: true,
    message: `Đã cập nhật trạng thái Barie làn ${payload.lane}: ${payload.state}`,
    data: payload,
  });
});

module.exports = router;

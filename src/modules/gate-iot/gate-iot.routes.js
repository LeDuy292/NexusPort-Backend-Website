'use strict';

const { Router } = require('express');
const { getIO } = require('../../config/socket');
const { sequelize } = require('../../config/database');
const { QueryTypes } = require('sequelize');

const router = Router();

/**
 * GET /api/gate/verify-plate?plate=47AB-294.69
 * Dùng bởi gate_runner.py (ESP32 OCR) để kiểm tra biển số có booking hợp lệ.
 * Logic: normalize plate (bỏ dấu gạch, chấm, khoảng trắng) rồi so sánh với DB.
 * Trả về { valid: true/false, booking: {...} | null }
 */
router.get('/verify-plate', async (req, res) => {
  try {
    const rawPlate = String(req.query.plate || '').trim();
    if (!rawPlate) {
      return res.json({ valid: false, reason: 'missing_plate' });
    }

    // Normalize: "47-AB 294.69" / "47AB29469" / "47AB-294.69" → "47AB29469"
    const normalized = rawPlate.toUpperCase().replace(/[\s\-.]/g, '');

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
    source: eventData.source || 'IoT_Camera',
    decision: eventData.decision || (detectedPlate ? 'SUCCESS' : 'FAIL'),
  };

  if (io) {
    io.to('gate:all').emit('gate:iot_scan', payload);
    io.emit('gate:iot_scan', payload);
  }

  return res.status(200).json({
    success: true,
    message: 'Đã nhận sự kiện IoT và phát Realtime tới Cổng kiểm soát.',
    data: payload,
  });
});

module.exports = router;

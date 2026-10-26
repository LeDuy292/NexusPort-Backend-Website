'use strict';

const { Router } = require('express');
const { getIO } = require('../../config/socket');

const router = Router();

/**
 * Endpoint tiếp nhận sự kiện quét biển số từ thiết bị IoT / AI Service
 * và phát Realtime qua Socket.IO tới màn hình Nhân viên Cổng (Gate Officer)
 */
router.post('/iot-event', (req, res) => {
  const io = getIO();
  const eventData = req.body;

  if (!eventData || !eventData.detectedPlate) {
    return res.status(400).json({
      success: false,
      message: 'Thiếu trường detectedPlate trong dữ liệu sự kiện IoT.',
    });
  }

  const payload = {
    gateCode: eventData.gateCode || 'GATE_IN_A',
    laneCode: eventData.laneCode || 'LANE_01',
    cameraId: eventData.cameraId || 'CAM_GATE_01',
    detectedPlate: eventData.detectedPlate,
    plateConfidence: eventData.plateConfidence || 0.98,
    vehicleDetected: eventData.vehicleDetected !== false,
    vehicleType: eventData.vehicleType || 'container_truck',
    vehiclePlateImageUrl: eventData.vehiclePlateImageUrl || null,
    overviewImageUrl: eventData.overviewImageUrl || null,
    timestamp: eventData.timestamp || new Date().toISOString(),
    verificationType: eventData.verificationType || 'AI_GATE_IN',
    source: eventData.source || 'IoT_Camera',
  };

  if (io) {
    // Phát tới room cổng
    io.to('gate:all').emit('gate:iot_scan', payload);
    // Đồng thời phát broadcast toàn hệ thống cho client cổng đang kết nối
    io.emit('gate:iot_scan', payload);
  }

  return res.status(200).json({
    success: true,
    message: 'Đã nhận sự kiện IoT và phát Realtime tới Cổng kiểm soát.',
    data: payload,
  });
});

module.exports = router;

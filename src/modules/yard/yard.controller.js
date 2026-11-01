'use strict';

const yardService = require('./yard.service');

/**
 * Lấy danh sách yêu cầu xuất container bãi.
 */
async function getGateOutRequests(req, res, next) {
  try {
    const result = await yardService.getGateOutRequests(req.query);
    res.status(200).json({
      success: true,
      data: result,
    });
  } catch (err) {
    next(err);
  }
}

/**
 * Lấy chi tiết yêu cầu xuất container bãi.
 */
async function getGateOutRequestById(req, res, next) {
  try {
    const result = await yardService.getGateOutRequestById(req.params.id);
    res.status(200).json({
      success: true,
      data: result,
    });
  } catch (err) {
    next(err);
  }
}

/**
 * Tạo / gán lệnh cẩu RTG cho yêu cầu xuất bãi (Bắt đầu pickup container).
 */
async function createLiftOrder(req, res, next) {
  try {
    const result = await yardService.createLiftOrder(req.params.id, req.body, req.user?.id);
    res.status(200).json({
      success: true,
      message: 'Đã gán lệnh nâng cẩu RTG và bắt đầu di chuyển tới vị trí container.',
      data: result,
    });
  } catch (err) {
    next(err);
  }
}

/**
 * Cập nhật bước tiến trình cẩu container.
 */
async function updateFlowStep(req, res, next) {
  try {
    const result = await yardService.updateFlowStep(req.params.id, req.body, req.user?.id);
    res.status(200).json({
      success: true,
      message: 'Đã cập nhật tiến trình nâng cẩu.',
      data: result,
    });
  } catch (err) {
    next(err);
  }
}

/**
 * Xác nhận container đã cẩu lên xe tải và kiểm tra đối soát thực địa hoàn tất.
 */
async function confirmLoadedOnTruck(req, res, next) {
  try {
    const result = await yardService.confirmLoadedOnTruck(req.params.id, req.body, req.user?.id);
    res.status(200).json({
      success: true,
      message: 'Xác nhận cẩu container lên xe thành công! Slot bãi đã được giải phóng và sẵn sàng cho xe xuất cổng.',
      data: result,
    });
  } catch (err) {
    next(err);
  }
}

/**
 * Tạo mới yêu cầu xuất container (khi Driver tới cổng hoặc Dispatcher tạo lệnh).
 */
async function createGateOutRequest(req, res, next) {
  try {
    const result = await yardService.createGateOutRequest(req.body, req.user?.id);
    res.status(201).json({
      success: true,
      message: 'Đã tạo yêu cầu xuất container bãi và thông báo cho Yard Staff.',
      data: result,
    });
  } catch (err) {
    next(err);
  }
}

module.exports = {
  getGateOutRequests,
  getGateOutRequestById,
  createLiftOrder,
  updateFlowStep,
  confirmLoadedOnTruck,
  createGateOutRequest,
};

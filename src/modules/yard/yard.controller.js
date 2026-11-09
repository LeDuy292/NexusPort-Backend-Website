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

/**
 * Lấy lịch sử các container từng chiếm một Slot hoặc các vị trí container từng lưu.
 */
async function getSlotOccupancyHistory(req, res, next) {
  try {
    const result = await yardService.getSlotOccupancyHistory(req.query);
    res.status(200).json({
      success: true,
      message: 'Lấy lịch sử vị trí bãi thành công.',
      data: result,
    });
  } catch (err) {
    next(err);
  }
}

/**
 * Xuất dữ liệu lịch sử Slot ra file CSV tải về.
 */
async function exportSlotOccupancyHistory(req, res, next) {
  try {
    const csvData = await yardService.exportSlotOccupancyHistoryCsv(req.query);
    const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
    const filename = `yard_slot_history_${timestamp}.csv`;

    res.setHeader('Content-Type', 'text/csv; charset=utf-8');
    res.setHeader('Content-Disposition', `attachment; filename="${filename}"`);
    res.status(200).send(csvData);
  } catch (err) {
    next(err);
  }
}

/**
 * Ghi nhận thay đổi vị trí container (lưu lịch sử).
 */
async function recordContainerMovement(req, res, next) {
  try {
    const result = await yardService.recordContainerMovement(req.body, req.user?.id);
    res.status(201).json({
      success: true,
      message: 'Đã cập nhật vị trí container và ghi nhận lịch sử.',
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
  getSlotOccupancyHistory,
  exportSlotOccupancyHistory,
  recordContainerMovement,
};


'use strict';

const gateOutDeliveryService = require('./gate-out-delivery.service');

/**
 * 1. Kiểm tra 5 điều kiện xuất container tại cổng vào theo thời gian thực
 */
async function checkEligibility(req, res, next) {
  try {
    const result = await gateOutDeliveryService.checkEligibility(req.body);
    res.status(200).json(result);
  } catch (err) {
    next(err);
  }
}

/**
 * 2. Lập phiếu EIR điện tử hướng ra (e-EIR Gate-Out) và xác nhận đối soát hiện trường
 */
async function issueGateOutEir(req, res, next) {
  try {
    const result = await gateOutDeliveryService.issueGateOutEir({
      ...req.body,
      userId: req.user?.id,
    });
    res.status(201).json(result);
  } catch (err) {
    next(err);
  }
}

/**
 * 3. Ghi nhận xe ra cổng ra, kết thúc tính lưu bãi, phát ContainerGatedOut
 */
async function confirmGateOutExit(req, res, next) {
  try {
    const result = await gateOutDeliveryService.confirmGateOutExit({
      ...req.body,
      userId: req.user?.id,
    });
    res.status(200).json(result);
  } catch (err) {
    next(err);
  }
}

/**
 * 4. Xử lý ngoại lệ trong quá trình xuất bãi (Sai cont, sai chì, EDoHeld, hư hỏng)
 */
async function handleException(req, res, next) {
  try {
    const result = await gateOutDeliveryService.handleException({
      ...req.body,
      userId: req.user?.id,
    });
    res.status(200).json(result);
  } catch (err) {
    next(err);
  }
}

module.exports = {
  checkEligibility,
  issueGateOutEir,
  confirmGateOutExit,
  handleException,
};

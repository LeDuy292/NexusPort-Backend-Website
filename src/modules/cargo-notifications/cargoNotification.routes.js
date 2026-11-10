'use strict';

const { Router } = require('express');
const { authenticate } = require('../../middlewares/authenticate');
const controller = require('./cargoNotification.controller');

const router = Router();

// 1. Nhận sự kiện từ Hãng tàu, Người 2 (TOS), Người 3 (Dịch vụ)
router.post('/events/ingest', controller.ingestEvent);

// 2. Tra cứu và quản lý thông báo cho chủ hàng
router.get('/', authenticate, controller.getNotifications);
router.get('/unread-count', authenticate, controller.getUnreadCount);
router.put('/read-all', authenticate, controller.markAllAsRead);
router.put('/:id/read', authenticate, controller.markAsRead);

// 3. To-Do / Action Center (Việc cần làm của chủ hàng)
router.get('/todo', authenticate, controller.getTodoList);

// 4. Document Tracking & Viewer (Theo dõi và xem chứng từ điện tử: EIR, e-D/O, B/L, Arrival Notice, Booking)
router.get('/documents', authenticate, controller.getDocuments);
router.get('/documents/:type/:id', authenticate, controller.getDocumentDetail);

// 5. Hàng đợi ngoại lệ: Người nhận chưa có tài khoản (Mục 4.2)
router.get('/unclaimed-consignees', authenticate, controller.getUnclaimedConsignees);
router.post('/unclaimed-consignees/:id/claim', authenticate, controller.claimConsignee);

module.exports = router;

'use strict';

const cargoNotificationService = require('./cargoNotification.service');

class CargoNotificationController {
  // POST /api/cargo-notifications/events/ingest
  async ingestEvent(req, res, next) {
    try {
      const result = await cargoNotificationService.ingestEvent(req.body);
      res.status(result.skipped ? 200 : 201).json({
        success: true,
        message: result.skipped ? 'Sự kiện đã được xử lý trước đó (Bỏ qua trùng lặp).' : 'Đã tiếp nhận sự kiện và tạo thông báo thành công.',
        data: result,
      });
    } catch (err) {
      next(err);
    }
  }

  // GET /api/cargo-notifications
  async getNotifications(req, res, next) {
    try {
      const { priority, direction, isRead, limit, offset } = req.query;
      const user = req.user || {};
      const companyId = req.query.companyId || user.CarrierId;
      const taxCode = req.query.taxCode;

      const result = await cargoNotificationService.getNotifications({
        userId: user.id,
        companyId,
        taxCode,
        priority,
        direction,
        isRead: isRead !== undefined ? isRead === 'true' : undefined,
        limit: limit || 30,
        offset: offset || 0,
      });

      res.json({
        success: true,
        data: result.items,
        pagination: {
          total: result.total,
          unreadCount: result.unreadCount,
          urgentCount: result.urgentCount,
          limit: Number(limit || 30),
          offset: Number(offset || 0),
        },
      });
    } catch (err) {
      next(err);
    }
  }

  // GET /api/cargo-notifications/unread-count
  async getUnreadCount(req, res, next) {
    try {
      const user = req.user || {};
      const companyId = req.query.companyId || user.CarrierId;
      const taxCode = req.query.taxCode;

      const result = await cargoNotificationService.getNotifications({
        userId: user.id,
        companyId,
        taxCode,
        isRead: false,
        limit: 1,
      });

      res.json({
        success: true,
        data: {
          unreadCount: result.unreadCount,
          urgentCount: result.urgentCount,
        },
      });
    } catch (err) {
      next(err);
    }
  }

  // PUT /api/cargo-notifications/:id/read
  async markAsRead(req, res, next) {
    try {
      const result = await cargoNotificationService.markAsRead(req.params.id);
      res.json({
        success: true,
        message: 'Đã đánh dấu thông báo là đã đọc.',
        data: result,
      });
    } catch (err) {
      next(err);
    }
  }

  // PUT /api/cargo-notifications/read-all
  async markAllAsRead(req, res, next) {
    try {
      const user = req.user || {};
      const companyId = req.body.companyId || user.CarrierId;
      const taxCode = req.body.taxCode;

      await cargoNotificationService.markAllAsRead({
        userId: user.id,
        companyId,
        taxCode,
      });

      res.json({
        success: true,
        message: 'Đã đánh dấu toàn bộ thông báo là đã đọc.',
      });
    } catch (err) {
      next(err);
    }
  }

  // GET /api/cargo-notifications/todo
  async getTodoList(req, res, next) {
    try {
      const user = req.user || {};
      const companyId = req.query.companyId || user.CarrierId;
      const taxCode = req.query.taxCode;
      const type = req.query.type || 'all';

      const todos = await cargoNotificationService.getTodoList({
        companyId,
        taxCode,
        type,
      });

      res.json({
        success: true,
        data: todos,
        summary: {
          total: todos.length,
          urgent: todos.filter(x => x.priority === 'urgent').length,
          export: todos.filter(x => x.direction === 'export').length,
          import: todos.filter(x => x.direction === 'import').length,
        },
      });
    } catch (err) {
      next(err);
    }
  }

  // GET /api/cargo-notifications/documents
  async getDocuments(req, res, next) {
    try {
      const { type = 'all', search = '', limit = 30, offset = 0 } = req.query;
      const result = await cargoNotificationService.getDocuments({
        type,
        search,
        limit: Number(limit),
        offset: Number(offset),
      });

      res.json({
        success: true,
        data: result.items,
        pagination: {
          total: result.total,
          limit: Number(limit),
          offset: Number(offset),
        },
      });
    } catch (err) {
      next(err);
    }
  }

  // GET /api/cargo-notifications/documents/:type/:id
  async getDocumentDetail(req, res, next) {
    try {
      const { type, id } = req.params;
      const detail = await cargoNotificationService.getDocumentDetail(type, id);
      if (detail.notFound) {
        return res.status(404).json({ success: false, message: detail.error });
      }
      res.json({
        success: true,
        data: detail,
      });
    } catch (err) {
      next(err);
    }
  }

  // GET /api/cargo-notifications/unclaimed-consignees
  async getUnclaimedConsignees(_req, res, next) {
    try {
      const queue = await cargoNotificationService.getUnclaimedConsignees();
      res.json({
        success: true,
        data: queue,
      });
    } catch (err) {
      next(err);
    }
  }

  // POST /api/cargo-notifications/unclaimed-consignees/:id/claim
  async claimConsignee(req, res, next) {
    try {
      const user = req.user || {};
      const result = await cargoNotificationService.claimConsignee({
        queueId: req.params.id,
        userId: user.id,
        companyId: req.body.companyId || user.CarrierId,
      });
      res.json({
        success: true,
        message: 'Đã gán người nhận cho chứng từ thành công.',
        data: result,
      });
    } catch (err) {
      next(err);
    }
  }

  // POST /api/cargo-notifications/draft-bl/confirm (Hành động mục 3.2 #9)
  async confirmDraftBL(req, res, next) {
    try {
      const { bookingNo, blNumber } = req.body;
      const user = req.user || {};
      const result = await cargoNotificationService.confirmDraftBL({
        bookingNo,
        blNumber,
        userId: user.id,
      });
      res.json({
        success: true,
        message: result.message,
        data: result.booking,
      });
    } catch (err) {
      next(err);
    }
  }

  // POST /api/cargo-notifications/vgm/submit (Hành động mục 3.2 #5)
  async submitVGM(req, res, next) {
    try {
      const { bookingNo, containerNo, vgmWeight, authorizedPerson, signatureMethod } = req.body;
      const result = await cargoNotificationService.submitVGM({
        bookingNo,
        containerNo,
        vgmWeight,
        authorizedPerson,
        signatureMethod,
      });
      res.json({
        success: true,
        message: result.message,
        data: result.vgmData,
      });
    } catch (err) {
      next(err);
    }
  }

  // POST /api/cargo-notifications/reminders/process (Tự động hóa nhắc việc)
  async processDueReminders(_req, res, next) {
    try {
      const result = await cargoNotificationService.processDueReminders();
      res.json({
        success: true,
        message: `Đã xử lý quét ${result.totalDue} nhắc hẹn, phát sinh ${result.processedCount} thông báo đến hạn.`,
        data: result,
      });
    } catch (err) {
      next(err);
    }
  }
}

module.exports = new CargoNotificationController();

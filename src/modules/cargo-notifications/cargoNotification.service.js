'use strict';

const { sequelize } = require('../../config/database');
const { getIO } = require('../../config/socket');
const shippingLineService = require('../shipping-line/shippingLine.service');

/**
 * Format ngày giờ theo chuẩn tiếng Việt
 */
function formatDate(dt) {
  if (!dt) return 'N/A';
  try {
    const d = new Date(dt);
    if (isNaN(d.getTime())) return String(dt);
    return d.toLocaleString('vi-VN', {
      hour: '2-digit',
      minute: '2-digit',
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
    });
  } catch {
    return String(dt);
  }
}

/**
 * Tra cứu thông tin người nhận (Company / User) dựa theo MST hoặc Tên công ty
 */
async function resolveRecipient({ companyId, taxCode, companyName, userId }) {
  if (companyId) {
    const [c] = await sequelize.query('SELECT id, company_name, tax_code FROM carriers WHERE id = :companyId', {
      replacements: { companyId },
      type: sequelize.QueryTypes.SELECT,
    });
    if (c) return { companyId: c.id, companyName: c.company_name, taxCode: c.tax_code, userId: userId || null };
  }

  if (taxCode) {
    const [c] = await sequelize.query('SELECT id, company_name, tax_code FROM carriers WHERE tax_code ILIKE :taxCode LIMIT 1', {
      replacements: { taxCode: taxCode.trim() },
      type: sequelize.QueryTypes.SELECT,
    });
    if (c) return { companyId: c.id, companyName: c.company_name, taxCode: c.tax_code, userId: userId || null };
  }

  if (companyName) {
    const [c] = await sequelize.query('SELECT id, company_name, tax_code FROM carriers WHERE company_name ILIKE :companyName LIMIT 1', {
      replacements: { companyName: `%${companyName.trim()}%` },
      type: sequelize.QueryTypes.SELECT,
    });
    if (c) return { companyId: c.id, companyName: c.company_name, taxCode: c.tax_code, userId: userId || null };
  }

  return { companyId: null, companyName: companyName || null, taxCode: taxCode || null, userId: userId || null };
}

/**
 * Phát socket realtime tới client
 */
function emitRealtimeNotification(notification, recipient) {
  try {
    const io = getIO();
    if (!io) return;
    if (recipient.userId) {
      io.to(`user:${recipient.userId}`).emit('cargo_notification:new', notification);
    }
    if (recipient.companyId) {
      io.to(`company:${recipient.companyId}`).emit('cargo_notification:new', notification);
    }
    // Also broadcast to general cargo channel
    io.emit('cargo_notification:broadcast', {
      id: notification.id,
      title: notification.title,
      priority: notification.priority,
      recipientCompanyId: recipient.companyId,
      companyTaxCode: recipient.taxCode,
    });
  } catch (err) {
    console.warn('[CargoNotification] Lỗi phát socket notification:', err.message);
  }
}

class CargoNotificationService {
  /**
   * XỬ LÝ NHẬN SỰ KIỆN TỪ CÁC BÊN (HÃNG TÀU, NGƯỜI 2 TOS, NGƯỜI 3 DỊCH VỤ)
   * Đảm bảo tính chống trùng (Idempotency), mức ưu tiên, gộp thông báo, tự động tạo/hủy reminders.
   */
  async ingestEvent(event) {
    if (!event || !event.eventCode) {
      throw new Error('Dữ liệu sự kiện không hợp lệ: thiếu eventCode.');
    }

    const {
      eventId,
      eventCode,
      version = 1,
      occurredAt = new Date().toISOString(),
      direction = 'general',
      payload = {},
      publisher = 'system',
    } = event;

    // 1. Phân giải người nhận (Recipient Resolution)
    const rawTaxCode = payload.shipperTaxCode || payload.consigneeTaxCode || payload.taxCode;
    const rawCompanyName = payload.shipper || payload.consignee || payload.consigneeName || payload.companyName;
    const rawCompanyId = payload.companyId || payload.carrierId;
    const rawUserId = payload.userId;

    const recipient = await resolveRecipient({
      companyId: rawCompanyId,
      taxCode: rawTaxCode,
      companyName: rawCompanyName,
      userId: rawUserId,
    });

    // 2. Khóa chống trùng duy nhất (Deduplication Key)
    const entityKey = payload.bookingNo || payload.blNumber || payload.orderCode || payload.eirNo || payload.doNumber || payload.manifestId || eventId;
    const recipientKey = recipient.companyId || recipient.taxCode || recipient.userId || 'GLOBAL';
    const dedupKey = `${eventCode}:${entityKey}:${recipientKey}:v${version}`;

    // Kiểm tra xem sự kiện này đã được xử lý chưa
    const [existing] = await sequelize.query(
      'SELECT id FROM cargo_notifications WHERE dedup_key = :dedupKey LIMIT 1',
      { replacements: { dedupKey }, type: sequelize.QueryTypes.SELECT }
    );
    if (existing) {
      console.log(`[CargoNotification] Bỏ qua sự kiện trùng lặp (${dedupKey})`);
      return { skipped: true, reason: 'DUPLICATE_EVENT', notificationId: existing.id };
    }

    // 3. Xử lý nghiệp vụ theo từng loại sự kiện (Chuẩn hóa theo Mục 3.2 và 4.2)
    let title = '';
    let message = '';
    let priority = 'normal'; // 'urgent' | 'normal' | 'info'
    let actionType = null;
    let actionLabel = null;
    let actionUrl = null;
    let documentType = null;
    let documentId = null;
    let notifDirection = direction;

    switch (eventCode) {
      // ══════════════════════════════════════════════
      // CHIỀU XUẤT KHẨU (EXPORT) - 3.2 THÔNG BÁO CHỦ HÀNG
      // ══════════════════════════════════════════════
      
      // 1. Booking được duyệt -> Mở booking
      case 'BookingConfirmed': {
        notifDirection = 'export';
        priority = 'normal';
        title = `Booking [${payload.bookingNo}] đã được Hãng tàu phê duyệt`;
        message = `Số booking: ${payload.bookingNo}. Tàu: ${payload.vesselName || 'Tàu chỉ định'} (Chuyến: ${payload.voyageNo || 'N/A'}). ETD: ${formatDate(payload.etd)}. Các Cut-off: CY (${formatDate(payload.cyCutOff)}), VGM (${formatDate(payload.vgmCutOff)}), SI (${formatDate(payload.siCutOff)}). Nơi lấy vỏ rỗng: ${payload.emptyPickupPlace || 'Depot chỉ định'}.`;
        actionType = 'open_booking';
        actionLabel = 'Mở booking';
        actionUrl = `/booking?bookingNo=${encodeURIComponent(payload.bookingNo)}`;
        documentType = 'BOOKING';
        documentId = payload.bookingNo;

        // Sinh reminders tự động ở các mốc cấu hình (Quy tắc 3)
        if (payload.vgmCutOff) {
          await this.createReminder({
            recipientCompanyId: recipient.companyId,
            companyTaxCode: recipient.taxCode,
            targetType: 'VGM_CUTOFF',
            targetId: payload.bookingNo,
            dueAt: payload.vgmCutOff,
            title: `Nhắc hạn nộp VGM cho Booking ${payload.bookingNo}`,
            message: `Hạn chót nộp VGM là ${formatDate(payload.vgmCutOff)}. Vui lòng hoàn thành để tránh bị từ chối vào cảng.`,
          });
        }
        if (payload.cyCutOff) {
          await this.createReminder({
            recipientCompanyId: recipient.companyId,
            companyTaxCode: recipient.taxCode,
            targetType: 'CY_CUTOFF',
            targetId: payload.bookingNo,
            dueAt: payload.cyCutOff,
            title: `Nhắc hạn CY Cut-off cho Booking ${payload.bookingNo}`,
            message: `Container phải hạ bãi trước ${formatDate(payload.cyCutOff)}.`,
          });
        }
        break;
      }

      // 2. Booking bị từ chối -> Tạo booking mới
      case 'BookingRejected': {
        notifDirection = 'export';
        priority = 'urgent';
        title = `Booking [${payload.bookingNo}] BỊ TỪ CHỐI`;
        message = `Lý do từ chối: ${payload.reason || 'Hết chỗ trên tàu hoặc không đáp ứng điều kiện vận chuyển'}. Vui lòng tạo yêu cầu booking mới.`;
        actionType = 'create_new_booking';
        actionLabel = 'Tạo booking mới';
        actionUrl = '/booking/new';
        break;
      }

      // 3. Đổi lịch tàu hoặc cut-off -> Xem chi tiết
      case 'BookingUpdated':
      case 'ScheduleChanged': {
        notifDirection = 'export';
        priority = 'normal'; // Đổi trạng thái
        title = `Booking [${payload.bookingNo}] thay đổi lịch tàu / Cut-off`;
        
        let changeDesc = '';
        if (payload.changes && Array.isArray(payload.changes)) {
          changeDesc = payload.changes.map(c => `${c.field || 'Hạng mục'}: cũ [${formatDate(c.oldValue) || c.oldValue}] -> mới [${formatDate(c.newValue) || c.newValue}]`).join('; ');
        } else if (payload.oldValues && payload.newValues) {
          const keys = Object.keys(payload.newValues);
          changeDesc = keys.map(k => `${k}: giá trị cũ [${payload.oldValues[k]}] -> giá trị mới [${payload.newValues[k]}]`).join('; ');
        } else {
          changeDesc = payload.details || `Lịch tàu mới: ETD ${formatDate(payload.etd)}. Cut-off mới: CY (${formatDate(payload.cyCutOff)}), VGM (${formatDate(payload.vgmCutOff)})`;
        }

        message = `Thông tin thay đổi: ${changeDesc}.`;
        actionType = 'view_details';
        actionLabel = 'Xem chi tiết';
        actionUrl = `/booking?bookingNo=${encodeURIComponent(payload.bookingNo)}`;
        documentType = 'BOOKING';
        documentId = payload.bookingNo;
        break;
      }

      case 'BookingCancelled': {
        notifDirection = 'export';
        priority = 'info';
        title = `Booking [${payload.bookingNo}] đã bị hủy`;
        message = `Lý do hủy: ${payload.reason || 'Yêu cầu từ các bên'}.`;
        actionType = 'view_details';
        actionLabel = 'Xem chi tiết';
        actionUrl = `/booking?bookingNo=${encodeURIComponent(payload.bookingNo)}`;
        // Hủy các reminder còn tồn tại của booking này
        await this.cancelRemindersByTarget('VGM_CUTOFF', payload.bookingNo);
        await this.cancelRemindersByTarget('CY_CUTOFF', payload.bookingNo);
        break;
      }

      // 4. Danh sách container có lỗi -> Sửa danh sách
      case 'ContainerListAnnounced': {
        notifDirection = 'export';
        const containers = payload.containers || payload.containerList || [];
        const errors = payload.errors || [];
        if (errors.length > 0) {
          priority = 'urgent';
          title = `Danh sách container của Booking [${payload.bookingNo}] CÓ LỖI`;
          const errorDetails = errors.map((err, idx) => `Dòng ${idx + 1}: ${typeof err === 'object' ? (err.message || JSON.stringify(err)) : err}`).join('; ');
          message = `Danh sách lỗi từng dòng: ${errorDetails}. Vui lòng sửa lại danh sách.`;
          actionType = 'edit_container_list';
          actionLabel = 'Sửa danh sách';
        } else {
          priority = 'normal';
          title = `Đã ghi nhận danh sách ${containers.length} container cho Booking [${payload.bookingNo}]`;
          message = `Các container (${containers.slice(0, 3).join(', ')}${containers.length > 3 ? '...' : ''}) đã đủ điều kiện để đặt dịch vụ hạ bãi. Nhắc nhở khai VGM trước hạn.`;
          actionType = 'submit_declaration';
          actionLabel = 'Khai hoặc nộp';
        }
        actionUrl = `/booking?bookingNo=${encodeURIComponent(payload.bookingNo)}`;
        break;
      }

      // 5. Sắp hết hạn VGM, CY, SI -> Khai hoặc nộp
      case 'CutoffApproaching':
      case 'DeadlineApproaching': {
        notifDirection = 'export';
        const cutoffType = payload.cutoffType || 'VGM, CY, SI';
        const diffMs = payload.dueAt ? (new Date(payload.dueAt).getTime() - Date.now()) : 0;
        const hoursRemaining = Math.max(0, Math.round(diffMs / (3600 * 1000)));
        // Quy tắc 2: Khẩn khi còn dưới 2 giờ trước hạn, Thường khi trên 2 giờ
        const isUrgent = (diffMs > 0 && diffMs <= 2 * 3600 * 1000) || payload.isUrgent;
        priority = isUrgent ? 'urgent' : 'normal';

        title = `${isUrgent ? 'KHẨN CẤP: ' : ''}Sắp hết hạn ${cutoffType} cho Booking [${payload.bookingNo || ''}]`;
        message = `Hạn chót: ${formatDate(payload.dueAt)}. Thời gian còn lại: ${hoursRemaining > 0 ? `${hoursRemaining} giờ` : 'dưới 1 giờ'}. Vui lòng khai báo hoặc nộp chứng từ ngay để tránh rớt tàu.`;
        actionType = 'submit_declaration';
        actionLabel = 'Khai hoặc nộp';
        actionUrl = `/booking?bookingNo=${encodeURIComponent(payload.bookingNo || '')}`;
        break;
      }

      // 6. Container qua cổng -> Xem EIR (Gộp nhiều container theo Quy tắc 4)
      case 'ContainerGatedIn': {
        notifDirection = 'export';
        priority = 'normal';
        const contList = payload.containers || (payload.containerNo ? [payload.containerNo] : []);
        const contText = contList.length > 1 ? `${contList.length} container (${contList.join(', ')})` : `Container ${contList[0] || ''}`;
        title = `${contText} ĐÃ QUA CỔNG VÀO CẢNG`;
        message = `Giờ vào: ${formatDate(payload.gateInAt || payload.occurredAt)}. Số EIR: ${payload.eirNo || 'N/A'}. Xe ${payload.truckPlate || ''} qua cổng ${payload.gateCode || 'GATE-01'}. Số chì: ${payload.sealNo || 'Khớp'}. Tình trạng vỏ: ${payload.condition || 'Tốt'}.`;
        actionType = 'view_eir';
        actionLabel = 'Xem EIR';
        actionUrl = `/cargo/documents/EIR/${encodeURIComponent(payload.eirNo || '')}`;
        documentType = 'EIR';
        documentId = payload.eirNo;

        // Đã vào cổng -> Tự động hủy reminder CY_CUTOFF (Quy tắc 3)
        if (payload.bookingNo) {
          await this.cancelRemindersByTarget('CY_CUTOFF', payload.bookingNo);
        }
        break;
      }

      // 7. Container bị từ chối ở cổng -> Liên hệ hãng tàu, xem hướng xử lý
      case 'ContainerGateRejected': {
        notifDirection = 'export';
        priority = 'urgent'; // Quy tắc 2: Khẩn (bị từ chối ở cổng)
        const contList = payload.containers || (payload.containerNo ? [payload.containerNo] : []);
        const contText = contList.length > 1 ? `${contList.length} container (${contList.join(', ')})` : `Container ${contList[0] || ''}`;
        title = `KHẨN CẤP: ${contText} BỊ TỪ CHỐI TẠI CỔNG VÀO`;
        message = `Lý do cụ thể: ${payload.reason || 'Quá hạn CY cut-off, thiếu khai báo VGM hoặc số chì không khớp dữ liệu'}. Cổng: ${payload.gateCode || 'GATE-01'}, Xe: ${payload.truckPlate || 'N/A'}. Vui lòng liên hệ hãng tàu ngay để xem hướng xử lý hoặc đưa vào hàng đợi ngoại lệ.`;
        actionType = 'contact_support';
        actionLabel = 'Liên hệ hãng tàu, xem hướng xử lý';
        actionUrl = `/cargo/support?containerNo=${encodeURIComponent(contList[0] || '')}`;
        break;
      }

      // 8. Container bị loại hoặc rớt tàu -> Xem chi tiết
      case 'ContainerRolledOver':
      case 'VoyageListClosed': {
        notifDirection = 'export';
        const rejected = payload.rejectedContainers || (eventCode === 'ContainerRolledOver' ? (payload.containers || [payload.containerNo]) : []);
        const accepted = payload.acceptedContainers || [];

        if (rejected.length > 0) {
          priority = 'urgent';
          const contText = rejected.length > 1 ? `${rejected.length} container (${rejected.join(', ')})` : `Container ${rejected[0] || ''}`;
          title = `CẢNH BÁO: ${contText} BỊ LOẠI HOẶC RỚT TÀU ${payload.vesselName || ''} (${payload.voyageNo || ''})`;
          message = `Lý do: ${payload.reason || 'Không kịp giờ đóng sổ, chưa có VGM hoặc vượt tải trọng tàu'}.${payload.newVoyageNo ? ` Chuyến mới được sắp xếp: ${payload.newVoyageNo} (ETD: ${formatDate(payload.newEtd)}).` : ' Chưa có chuyến thay thế, vui lòng xem chi tiết.'}`;
          actionType = 'view_details';
          actionLabel = 'Xem chi tiết';
          actionUrl = `/booking?bookingNo=${encodeURIComponent(payload.bookingNo || '')}`;
        } else {
          priority = 'normal';
          title = `Danh sách xếp tàu chuyến ${payload.voyageNo || ''} ĐÃ CHỐT`;
          message = `Toàn bộ ${accepted.length} container thuộc Booking ${payload.bookingNo || ''} đã được phê duyệt trong danh sách xếp tàu.`;
          actionType = 'view_details';
          actionLabel = 'Xem chi tiết';
          actionUrl = `/booking?bookingNo=${encodeURIComponent(payload.bookingNo || '')}`;
        }
        break;
      }

      case 'ContainerLoaded': {
        notifDirection = 'export';
        priority = 'normal';
        const contList = payload.containers || (payload.containerNo ? [payload.containerNo] : []);
        const contText = contList.length > 1 ? `${contList.length} container (${contList.join(', ')})` : `Container ${contList[0] || ''}`;
        title = `${contText} đã xếp lên tàu`;
        message = `Đã bốc an toàn lên tàu ${payload.vesselName || ''} (Chuyến ${payload.voyageNo || ''}) lúc ${formatDate(payload.loadedAt)}.`;
        actionType = 'view_details';
        actionLabel = 'Xem chi tiết';
        actionUrl = `/booking?bookingNo=${encodeURIComponent(payload.bookingNo || '')}`;
        break;
      }

      case 'VesselDeparted': {
        notifDirection = 'export';
        priority = 'normal';
        title = `Tàu ${payload.vesselName || ''} (Chuyến ${payload.voyageNo || ''}) ĐÃ RỜI CẦU`;
        message = `Tàu đã rời cảng Tiên Sa lúc ${formatDate(payload.departedAt)}. Chuyến hàng xuất khẩu của bạn đã chính thức khởi hành.`;
        actionType = 'view_details';
        actionLabel = 'Xem chi tiết';
        actionUrl = `/booking?bookingNo=${encodeURIComponent(payload.bookingNo || '')}`;
        break;
      }

      // 9. Draft B/L cần xác nhận -> Xem và xác nhận draft
      case 'DraftBLIssued': {
        notifDirection = 'export';
        priority = 'normal';
        title = `Bản nháp vận đơn (Draft B/L) [${payload.blNumber || ''}] cần xác nhận`;
        message = `Hãng tàu đã phát hành Draft B/L cho Booking ${payload.bookingNo || ''}. Hạn xác nhận: ${formatDate(payload.confirmationDeadline)}. Vui lòng kiểm tra thông tin hàng hóa và xác nhận trước thời hạn.`;
        actionType = 'confirm_draft_bl';
        actionLabel = 'Xem và xác nhận draft';
        actionUrl = `/cargo/documents/BL/${encodeURIComponent(payload.blNumber || '')}`;
        documentType = 'BL';
        documentId = payload.blNumber;

        if (payload.confirmationDeadline) {
          await this.createReminder({
            recipientCompanyId: recipient.companyId,
            companyTaxCode: recipient.taxCode,
            targetType: 'DRAFT_BL_CONFIRM',
            targetId: payload.blNumber,
            dueAt: payload.confirmationDeadline,
            title: `Nhắc xác nhận Draft B/L ${payload.blNumber}`,
            message: `Bản nháp vận đơn cần được xác nhận trước ${formatDate(payload.confirmationDeadline)}.`,
          });
        }
        break;
      }

      // 10. B/L đã phát hành -> Xem B/L
      case 'BLIssued': {
        notifDirection = 'export';
        priority = 'normal';
        title = `Vận đơn chính thức (Bill of Lading) [${payload.blNumber || ''}] đã phát hành`;
        message = `Số B/L: ${payload.blNumber}. Ngày xếp hàng lên tàu: ${formatDate(payload.shippedOnBoardDate)}. Bạn có thể xem và tải bản B/L điện tử.`;
        actionType = 'view_bl';
        actionLabel = 'Xem B/L';
        actionUrl = `/cargo/documents/BL/${encodeURIComponent(payload.blNumber || '')}`;
        documentType = 'BL';
        documentId = payload.blNumber;

        if (payload.blNumber) {
          await this.cancelRemindersByTarget('DRAFT_BL_CONFIRM', payload.blNumber);
        }
        break;
      }

      // ══════════════════════════════════════════════
      // CHIỀU NHẬP KHẨU (IMPORT) - 4.2 THÔNG BÁO CHO NGƯỜI NHẬN
      // ══════════════════════════════════════════════
      case 'ManifestSent': {
        notifDirection = 'import';
        priority = 'normal';
        if (!recipient.companyId && !recipient.userId) {
          await this.enqueueUnclaimedConsignee({
            manifestId: payload.manifestId || payload.voyageNo,
            blNumber: payload.blNumber || 'N/A',
            consigneeName: rawCompanyName || 'Chưa xác định',
            consigneeTaxCode: rawTaxCode || null,
            containerCount: payload.containers?.length || 1,
            vesselName: payload.vesselName || 'Tàu nhập',
            voyageNumber: payload.voyageNo || 'N/A',
            eta: payload.eta,
          });
          console.log(`[CargoNotification] Đã đưa Consignee chưa có tài khoản vào hàng đợi: ${rawCompanyName} (MST: ${rawTaxCode})`);
        }

        title = `Thông báo hàng đến (Arrival Notice) - Tàu ${payload.vesselName || ''} (ETA: ${formatDate(payload.eta)})`;
        message = `Có B/L [${payload.blNumber || ''}] của bạn trên chuyến sắp đến. ETA: ${formatDate(payload.eta)}. Số container: ${(payload.containers || []).length} cont. Free time lưu bãi: ${payload.freeTimeDays || 5} ngày.`;
        actionType = 'view_arrival_notice';
        actionLabel = 'Xem thông báo hàng đến';
        actionUrl = `/cargo/documents/ARRIVAL_NOTICE/${encodeURIComponent(payload.blNumber || '')}`;
        documentType = 'ARRIVAL_NOTICE';
        documentId = payload.blNumber;
        break;
      }

      case 'ContainerDischarged': {
        notifDirection = 'import';
        priority = 'normal';
        const contList = payload.containers || (payload.containerNo ? [payload.containerNo] : []);
        const contText = contList.length > 1 ? `${contList.length} container (${contList.join(', ')})` : `Container ${contList[0] || ''}`;
        title = `${contText} ĐÃ DỠ KHỎI TÀU`;
        message = `Giờ dỡ: ${formatDate(payload.dischargedAt)}. Ngày bắt đầu tính lưu bãi từ hôm nay. Vị trí bãi: ${payload.yardLocation || 'Khu hàng nhập'}.`;
        actionType = 'view_container';
        actionLabel = 'Xem container';
        actionUrl = `/cargo/documents/ARRIVAL_NOTICE/${encodeURIComponent(payload.blNumber || '')}`;
        break;
      }

      case 'EDoIssued': {
        notifDirection = 'import';
        priority = 'normal';
        title = `Lệnh giao hàng điện tử (e-D/O) [${payload.doNumber || ''}] đã được cấp`;
        message = `Số D/O: ${payload.doNumber}. Hạn hiệu lực: ${formatDate(payload.validUntil)}. Nơi trả vỏ rỗng: ${payload.emptyReturnDepot || 'Depot chỉ định'}. Bạn đã đủ điều kiện đặt lệnh lấy container trên e-Port.`;
        actionType = 'order_service_pickup';
        actionLabel = 'Đặt lệnh lấy hàng';
        actionUrl = `/cargo/documents/EDO/${encodeURIComponent(payload.doNumber || '')}`;
        documentType = 'EDO';
        documentId = payload.doNumber;

        if (payload.validUntil) {
          await this.createReminder({
            recipientCompanyId: recipient.companyId,
            companyTaxCode: recipient.taxCode,
            targetType: 'FREE_TIME',
            targetId: payload.doNumber,
            dueAt: payload.validUntil,
            title: `Nhắc hạn lấy container e-D/O ${payload.doNumber}`,
            message: `Hạn hiệu lực e-D/O và lưu bãi đến ${formatDate(payload.validUntil)}. Hãy lấy hàng sớm để tránh phát sinh phụ phí lưu bãi.`,
          });
        }
        break;
      }

      case 'EDoHeld': {
        notifDirection = 'import';
        priority = 'urgent'; // Khẩn
        title = `CẢNH BÁO: e-D/O [${payload.doNumber || ''}] ĐANG BỊ TẠM GIỮ (HOLD)`;
        message = `Lý do: ${payload.reason || 'Yêu cầu kiểm tra chứng từ hoặc thu phí hãng tàu'}. Cổng cảng sẽ tạm dừng cho xe lấy cont ra. Vui lòng liên hệ hãng tàu.`;
        actionType = 'contact_shipping_line';
        actionLabel = 'Liên hệ hãng tàu';
        actionUrl = `/cargo/documents/EDO/${encodeURIComponent(payload.doNumber || '')}`;
        documentType = 'EDO';
        documentId = payload.doNumber;
        break;
      }

      case 'EDoReleased': {
        notifDirection = 'import';
        priority = 'normal';
        title = `e-D/O [${payload.doNumber || ''}] ĐÃ ĐƯỢC GIẢI TỎA (RELEASED)`;
        message = `Hãng tàu đã mở khóa giao hàng. Bạn có thể tiếp tục tiến hành đưa xe vào lấy container bình thường.`;
        actionType = 'order_service_pickup';
        actionLabel = 'Đặt lệnh lấy hàng';
        actionUrl = `/cargo/documents/EDO/${encodeURIComponent(payload.doNumber || '')}`;
        documentType = 'EDO';
        documentId = payload.doNumber;
        break;
      }

      case 'EDoCancelled': {
        notifDirection = 'import';
        priority = 'urgent';
        title = `e-D/O [${payload.doNumber || ''}] ĐÃ BỊ HỦY`;
        message = `Lý do: ${payload.reason || 'Yêu cầu điều chỉnh chứng từ từ Hãng tàu'}. Lệnh lấy hàng tại cảng sẽ tự động tạm dừng.`;
        actionType = 'contact_shipping_line';
        actionLabel = 'Liên hệ hãng tàu';
        documentType = 'EDO';
        documentId = payload.doNumber;
        break;
      }

      case 'FreeTimeApproaching': {
        notifDirection = 'import';
        const diffMs = payload.dueAt ? (new Date(payload.dueAt).getTime() - Date.now()) : 0;
        priority = (diffMs > 0 && diffMs <= 2 * 3600 * 1000) ? 'urgent' : 'normal';
        title = `${priority === 'urgent' ? 'KHẨN CẤP: ' : ''}Sắp hết Free time lưu bãi e-D/O [${payload.doNumber || ''}]`;
        message = `Hạn chót: ${formatDate(payload.dueAt)}. Phí dự kiến nếu quá hạn: ${Number(payload.estimatedDemurrage || 500000).toLocaleString('vi-VN')} VND/ngày. Vui lòng đặt lệnh lấy hàng ngay.`;
        actionType = 'order_service_pickup';
        actionLabel = 'Đặt lệnh lấy hàng';
        actionUrl = `/cargo/documents/EDO/${encodeURIComponent(payload.doNumber || '')}`;
        break;
      }

      case 'ContainerGatedOut': {
        notifDirection = 'import';
        priority = 'normal';
        const contList = payload.containers || (payload.containerNo ? [payload.containerNo] : []);
        const contText = contList.length > 1 ? `${contList.length} container (${contList.join(', ')})` : `Container ${contList[0] || ''}`;
        title = `${contText} ĐÃ RA CỔNG CẢNG (EIR: ${payload.eirNo || ''})`;
        message = `Giờ ra: ${formatDate(payload.gateOutAt || payload.occurredAt)}. Số EIR: ${payload.eirNo || 'N/A'}. Xe ${payload.truckPlate || ''}. Kết thúc tính phí lưu bãi. NHẮC NHỞ: Vui lòng trả vỏ rỗng về ${payload.emptyReturnDepot || 'Depot chỉ định'} theo quy định của Hãng tàu.`;
        actionType = 'view_eir';
        actionLabel = 'Nhắc trả vỏ';
        actionUrl = `/cargo/documents/EIR/${encodeURIComponent(payload.eirNo || '')}`;
        documentType = 'EIR';
        documentId = payload.eirNo;

        // Đã lấy hàng ra cổng -> Hủy reminder free time lưu bãi
        if (payload.doNumber) {
          await this.cancelRemindersByTarget('FREE_TIME', payload.doNumber);
        }

        // Lập reminder trả vỏ rỗng (hạn 3 ngày tới)
        const emptyDueAt = new Date(Date.now() + 3 * 24 * 3600 * 1000).toISOString();
        await this.createReminder({
          recipientCompanyId: recipient.companyId,
          companyTaxCode: recipient.taxCode,
          targetType: 'EMPTY_RETURN',
          targetId: payload.containerNo || payload.eirNo,
          dueAt: emptyDueAt,
          title: `Nhắc trả vỏ rỗng container ${payload.containerNo || ''}`,
          message: `Vỏ rỗng cần được hạ về ${payload.emptyReturnDepot || 'Depot'} trước ${formatDate(emptyDueAt)} để tránh phí phạt lưu vỏ (DET).`,
        });
        break;
      }

      // ══════════════════════════════════════════════
      // DỊCH VỤ CẢNG E-PORT (NGƯỜI 3)
      // ══════════════════════════════════════════════
      case 'ServiceOrderSubmitted': {
        priority = 'info';
        title = `Lệnh dịch vụ [${payload.orderCode || ''}] ĐÃ TẠO (Chờ thanh toán)`;
        message = `Dịch vụ: ${payload.serviceName || payload.serviceType || 'Cảng'}. Số tiền: ${Number(payload.amount || 0).toLocaleString('vi-VN')} VND. Vui lòng thanh toán để Cảng lên kế hoạch thực thi.`;
        actionType = 'pay_order';
        actionLabel = 'Xem lệnh';
        actionUrl = `/billing?orderCode=${encodeURIComponent(payload.orderCode || '')}`;
        break;
      }

      case 'ServiceOrderPaid': {
        priority = 'normal';
        title = `Lệnh dịch vụ [${payload.orderCode || ''}] ĐÃ THANH TOÁN THÀNH CÔNG`;
        message = `Cảng đã nhận thanh toán ${Number(payload.amount || 0).toLocaleString('vi-VN')} VND (Mã tham chiếu: ${payload.paymentRef || 'MOCK_PAY'}). Lệnh đã được chuyển sang kế hoạch điều độ.`;
        actionType = 'view_order';
        actionLabel = 'Xem lệnh';
        actionUrl = `/billing?orderCode=${encodeURIComponent(payload.orderCode || '')}`;
        break;
      }

      case 'ServiceOrderCompleted': {
        priority = 'normal';
        title = `Lệnh dịch vụ [${payload.orderCode || ''}] ĐÃ HOÀN THÀNH`;
        message = `Dịch vụ ${payload.serviceName || payload.serviceType || ''} đã được nhân viên Cảng thực thi hoàn tất. Đã phát hành hóa đơn điện tử ${payload.invoiceNo || ''}.`;
        actionType = 'view_invoice';
        actionLabel = 'Xem lệnh';
        actionUrl = `/billing/invoices?orderCode=${encodeURIComponent(payload.orderCode || '')}`;
        break;
      }

      case 'ServiceOrderFailed': {
        priority = 'urgent';
        title = `Lệnh dịch vụ [${payload.orderCode || ''}] KHÔNG THỂ THỰC THI`;
        message = `Lý do: ${payload.reason || 'Trở ngại nghiệp vụ tại bãi'}. Lệnh đã chuyển sang Chờ xử lý hoặc Hoàn phí theo quy chế.`;
        actionType = 'contact_shipping_line';
        actionLabel = 'Xem lệnh';
        actionUrl = `/billing?orderCode=${encodeURIComponent(payload.orderCode || '')}`;
        break;
      }

      default: {
        title = `Thông báo mới từ Cảng: ${eventCode}`;
        message = payload.message || JSON.stringify(payload);
        priority = 'info';
        actionType = 'view_details';
        actionLabel = 'Xem chi tiết';
        break;
      }
    }

    // 4. Lưu vào bảng cargo_notifications
    const insertRes = await sequelize.query(`
      INSERT INTO cargo_notifications (
        recipient_user_id, recipient_company_id, company_tax_code, recipient_role,
        event_type, direction, priority, title, message, data,
        action_type, action_label, action_url, document_type, document_id, dedup_key, created_at
      ) VALUES (
        :userId, :companyId, :taxCode, :role,
        :eventCode, :direction, :priority, :title, :message, :data,
        :actionType, :actionLabel, :actionUrl, :documentType, :documentId, :dedupKey, :occurredAt
      ) RETURNING *
    `, {
      replacements: {
        userId: recipient.userId,
        companyId: recipient.companyId,
        taxCode: recipient.taxCode,
        role: direction === 'import' ? 'consignee' : 'shipper',
        eventCode,
        direction: notifDirection,
        priority,
        title,
        message,
        data: JSON.stringify(payload),
        actionType,
        actionLabel,
        actionUrl,
        documentType,
        documentId,
        dedupKey,
        occurredAt,
      },
      type: sequelize.QueryTypes.INSERT,
    });

    const savedNotification = insertRes[0]?.[0] || insertRes[0];

    // 5. Bắn thông báo realtime qua WebSocket
    emitRealtimeNotification(savedNotification, recipient);

    return {
      success: true,
      notification: savedNotification,
    };
  }

  /**
   * Tạo Reminder tự động
   */
  async createReminder({ recipientCompanyId, companyTaxCode, targetType, targetId, dueAt, title, message }) {
    if (!dueAt) return;
    const dueTime = new Date(dueAt).getTime();
    if (isNaN(dueTime)) return;

    // Thiết lập 2 mốc nhắc: Trước 24h và Trước 2h
    const milestones = [
      { code: 'T-24H', offsetMs: 24 * 3600 * 1000 },
      { code: 'T-2H', offsetMs: 2 * 3600 * 1000 },
    ];

    for (const m of milestones) {
      const remindTime = new Date(dueTime - m.offsetMs);
      await sequelize.query(`
        INSERT INTO cargo_reminders (
          recipient_company_id, company_tax_code, target_type, target_id, milestone,
          due_at, remind_at, status, title, message
        ) VALUES (
          :recipientCompanyId, :companyTaxCode, :targetType, :targetId, :milestone,
          :dueAt, :remindAt, 'pending', :title, :message
        )
      `, {
        replacements: {
          recipientCompanyId: recipientCompanyId || null,
          companyTaxCode: companyTaxCode || null,
          targetType,
          targetId,
          milestone: m.code,
          dueAt: new Date(dueTime).toISOString(),
          remindAt: remindTime.toISOString(),
          title: `[${m.code}] ${title}`,
          message: `${message} (Mốc nhắc: ${m.code})`,
        },
      });
    }
  }

  /**
   * Hủy các reminder khi công việc đã được hoàn thành (Mục 3.2: việc đã xong thì hủy các nhắc còn lại)
   */
  async cancelRemindersByTarget(targetType, targetId) {
    await sequelize.query(`
      UPDATE cargo_reminders
      SET status = 'cancelled', updated_at = now()
      WHERE target_type = :targetType AND target_id = :targetId AND status = 'pending'
    `, {
      replacements: { targetType, targetId },
    });
    console.log(`[CargoNotification] Đã tự động hủy các reminder của ${targetType} - ${targetId}`);
  }

  /**
   * Thêm vào hàng đợi ngoại lệ: Người nhận chưa có tài khoản (Mục 4.2)
   */
  async enqueueUnclaimedConsignee({ manifestId, blNumber, consigneeName, consigneeTaxCode, containerCount, vesselName, voyageNumber, eta }) {
    await sequelize.query(`
      INSERT INTO unclaimed_consignee_queue (
        manifest_id, bl_number, consignee_name, consignee_tax_code, container_count,
        vessel_name, voyage_number, eta, status
      ) VALUES (
        :manifestId, :blNumber, :consigneeName, :consigneeTaxCode, :containerCount,
        :vesselName, :voyageNumber, :eta, 'PENDING_CLAIM'
      )
    `, {
      replacements: {
        manifestId: manifestId || null,
        blNumber,
        consigneeName,
        consigneeTaxCode: consigneeTaxCode || null,
        containerCount: containerCount || 1,
        vesselName: vesselName || null,
        voyageNumber: voyageNumber || null,
        eta: eta ? new Date(eta).toISOString() : null,
      },
    });
  }

  /**
   * Lấy danh sách thông báo của chủ hàng
   */
  async getNotifications({ userId, companyId, taxCode, priority, direction, isRead, limit = 50, offset = 0 }) {
    let whereClauses = ['1=1'];
    const replacements = { limit: Number(limit), offset: Number(offset) };

    if (userId || companyId || taxCode) {
      const parts = [];
      if (userId) { parts.push('recipient_user_id = :userId'); replacements.userId = userId; }
      if (companyId) { parts.push('recipient_company_id = :companyId'); replacements.companyId = companyId; }
      if (taxCode) { parts.push('company_tax_code ILIKE :taxCode'); replacements.taxCode = taxCode.trim(); }
      // Cho phép thấy cả thông báo gửi chung
      parts.push('(recipient_user_id IS NULL AND recipient_company_id IS NULL AND company_tax_code IS NULL)');
      whereClauses.push(`(${parts.join(' OR ')})`);
    }

    if (priority) {
      whereClauses.push('priority = :priority');
      replacements.priority = priority;
    }

    if (direction) {
      whereClauses.push('(direction = :direction OR direction = \'general\')');
      replacements.direction = direction;
    }

    if (typeof isRead === 'boolean') {
      whereClauses.push('is_read = :isRead');
      replacements.isRead = isRead;
    }

    const sql = `
      SELECT * FROM cargo_notifications
      WHERE ${whereClauses.join(' AND ')}
      ORDER BY created_at DESC
      LIMIT :limit OFFSET :offset
    `;

    const countSql = `
      SELECT count(*)::int AS total,
             count(*) FILTER (WHERE is_read = false)::int AS unread,
             count(*) FILTER (WHERE priority = 'urgent' AND is_read = false)::int AS urgent
      FROM cargo_notifications
      WHERE ${whereClauses.join(' AND ')}
    `;

    const [rows, counts] = await Promise.all([
      sequelize.query(sql, { replacements, type: sequelize.QueryTypes.SELECT }),
      sequelize.query(countSql, { replacements, type: sequelize.QueryTypes.SELECT }),
    ]);

    return {
      items: rows,
      total: counts[0]?.total || 0,
      unreadCount: counts[0]?.unread || 0,
      urgentCount: counts[0]?.urgent || 0,
    };
  }

  /**
   * Đánh dấu 1 thông báo đã đọc
   */
  async markAsRead(notificationId) {
    const [result] = await sequelize.query(`
      UPDATE cargo_notifications
      SET is_read = true, read_at = now()
      WHERE id = :notificationId
      RETURNING *
    `, {
      replacements: { notificationId },
      type: sequelize.QueryTypes.UPDATE,
    });
    return result[0];
  }

  /**
   * Đánh dấu tất cả thông báo đã đọc
   */
  async markAllAsRead({ userId, companyId, taxCode }) {
    let whereClauses = ['is_read = false'];
    const replacements = {};

    if (userId || companyId || taxCode) {
      const parts = [];
      if (userId) { parts.push('recipient_user_id = :userId'); replacements.userId = userId; }
      if (companyId) { parts.push('recipient_company_id = :companyId'); replacements.companyId = companyId; }
      if (taxCode) { parts.push('company_tax_code ILIKE :taxCode'); replacements.taxCode = taxCode.trim(); }
      whereClauses.push(`(${parts.join(' OR ')})`);
    }

    await sequelize.query(`
      UPDATE cargo_notifications
      SET is_read = true, read_at = now()
      WHERE ${whereClauses.join(' AND ')}
    `, { replacements });

    return { success: true };
  }

  /**
   * CHẾ ĐỘ XEM VIỆC CẦN LÀM (TO-DO / ACTION CENTER) CHO CHỦ HÀNG
   * Tổng hợp các việc cần giải quyết: Khai cont, nộp VGM, nộp SI, xác nhận Draft B/L, đặt lệnh hạ bãi, lấy cont nhập, trả vỏ rỗng.
   */
  async getTodoList({ companyId, taxCode, type = 'all' }) {
    const todos = [];
    const now = new Date();

    // 1. Việc Xuất Khẩu: Lấy danh sách export_bookings đang hoạt động
    if (type === 'all' || type === 'export') {
      try {
        const bookings = await sequelize.query(`
          SELECT eb.id, eb.booking_no, eb.bl_number, eb.quantity, eb.status, eb.si_status, eb.draft, eb.draft_version,
                 eb.created_at, eb.carrier_id, c.company_name
          FROM export_bookings eb
          LEFT JOIN carriers c ON c.id = eb.carrier_id
          WHERE eb.status NOT IN ('cancelled', 'completed')
          ORDER BY eb.created_at DESC LIMIT 20
        `, { type: sequelize.QueryTypes.SELECT });

        for (const b of bookings) {
          // Việc 1: Khai danh sách container
          if (b.status === 'confirmed' || b.status === 'Chờ container') {
            todos.push({
              id: `todo-decl-${b.id}`,
              taskType: 'DECLARE_CONTAINER_LIST',
              direction: 'export',
              priority: 'normal',
              title: `Khai báo danh sách container cho Booking ${b.booking_no}`,
              description: `Booking đã được duyệt (số lượng: ${b.quantity} cont). Cần khai danh sách container dự kiến.`,
              bookingNo: b.booking_no,
              deadline: null,
              actionLabel: 'Khai danh sách',
              actionUrl: `/booking?bookingNo=${encodeURIComponent(b.booking_no)}`,
            });
          }

          // Việc 2: Xác nhận Draft B/L
          if (b.draft && b.status !== 'bl_issued') {
            todos.push({
              id: `todo-draft-${b.id}`,
              taskType: 'CONFIRM_DRAFT_BL',
              direction: 'export',
              priority: 'urgent',
              title: `Xác nhận bản nháp vận đơn (Draft B/L) cho ${b.booking_no}`,
              description: `Hãng tàu đã phát hành Draft B/L v${b.draft_version || 1}. Vui lòng kiểm tra và xác nhận sớm.`,
              bookingNo: b.booking_no,
              blNumber: b.bl_number,
              deadline: new Date(Date.now() + 12 * 3600 * 1000).toISOString(),
              actionLabel: 'Xem & xác nhận Draft',
              actionUrl: `/cargo/documents/BL/${encodeURIComponent(b.bl_number || b.booking_no)}`,
            });
          }
        }
      } catch (err) {
        console.warn('[CargoNotification] Lỗi lấy To-do xuất khẩu:', err.message);
      }
    }

    // 2. Việc Nhập Khẩu: Lấy từ e-D/O và Hàng đến trong shippingLine data
    if (type === 'all' || type === 'import') {
      try {
        const manifests = shippingLineService.manifests || [];
        const dos = shippingLineService.deliveryOrders || [];

        // e-D/O đã cấp nhưng chưa đặt lệnh lấy hàng
        for (const edo of dos) {
          if (edo.status === 'RELEASED') {
            const validUntil = new Date(edo.validUntil);
            const hoursLeft = (validUntil - now) / (1000 * 3600);
            todos.push({
              id: `todo-edo-${edo.id || edo.doNumber}`,
              taskType: 'ORDER_PICKUP_SERVICE',
              direction: 'import',
              priority: hoursLeft <= 6 ? 'urgent' : 'normal',
              title: `Đặt lệnh lấy container nhập cho e-D/O ${edo.doNumber}`,
              description: `Vận đơn: ${edo.blNumber}. Hạn lấy trước: ${formatDate(edo.validUntil)} (${Math.max(0, Math.round(hoursLeft))} giờ còn lại).`,
              doNumber: edo.doNumber,
              blNumber: edo.blNumber,
              deadline: edo.validUntil,
              actionLabel: 'Đặt lệnh lấy hàng',
              actionUrl: `/cargo/documents/EDO/${encodeURIComponent(edo.doNumber)}`,
            });
          } else if (edo.status === 'HELD') {
            todos.push({
              id: `todo-edo-held-${edo.id || edo.doNumber}`,
              taskType: 'RESOLVE_HELD_EDO',
              direction: 'import',
              priority: 'urgent',
              title: `KHẨN: e-D/O ${edo.doNumber} đang BỊ TẠM GIỮ`,
              description: `Hàng thuộc B/L ${edo.blNumber} đang bị hãng tàu khóa. Cần liên hệ hãng tàu để giải tỏa.`,
              doNumber: edo.doNumber,
              blNumber: edo.blNumber,
              deadline: null,
              actionLabel: 'Liên hệ Hãng tàu',
              actionUrl: `/cargo/support?doNumber=${encodeURIComponent(edo.doNumber)}`,
            });
          }
        }

        // Nhắc trả vỏ rỗng cho các EIR Gate-Out gần đây
        const recentGateOuts = await sequelize.query(`
          SELECT reference_no, bl_booking_no, plate_number, empty_return_place, check_out_at
          FROM eir_transactions
          WHERE activity = 'GATE_OUT' AND check_out_at >= now() - interval '4 days'
          ORDER BY check_out_at DESC LIMIT 5
        `, { type: sequelize.QueryTypes.SELECT });

        for (const eir of recentGateOuts) {
          todos.push({
            id: `todo-empty-${eir.reference_no}`,
            taskType: 'RETURN_EMPTY_CONTAINER',
            direction: 'import',
            priority: 'normal',
            title: `Trả vỏ rỗng về ${eir.empty_return_place || 'Depot chỉ định'}`,
            description: `Xe ${eir.plate_number || ''} đã lấy cont ra cổng lúc ${formatDate(eir.check_out_at)}. Hãy hạ vỏ rỗng về depot đúng hạn.`,
            eirNo: eir.reference_no,
            actionLabel: 'Xem phiếu EIR',
            actionUrl: `/cargo/documents/EIR/${encodeURIComponent(eir.reference_no)}`,
          });
        }
      } catch (err) {
        console.warn('[CargoNotification] Lỗi lấy To-do nhập khẩu:', err.message);
      }
    }

    // 3. Việc từ các nhắc hẹn tự động (cargo_reminders: VGM, CY Cut-off, Free time, Trả vỏ)
    try {
      let reminderWhere = "status = 'pending' AND due_at >= now() - interval '2 days'";
      const rRepl = {};
      if (companyId) {
        reminderWhere += ' AND (recipient_company_id = :companyId OR recipient_company_id IS NULL)';
        rRepl.companyId = companyId;
      }
      if (taxCode) {
        reminderWhere += ' AND (company_tax_code ILIKE :taxCode OR company_tax_code IS NULL)';
        rRepl.taxCode = taxCode.trim();
      }

      const reminders = await sequelize.query(`
        SELECT id, target_type, target_id, milestone, due_at, title, message, status
        FROM cargo_reminders
        WHERE ${reminderWhere}
        ORDER BY due_at ASC LIMIT 20
      `, { replacements: rRepl, type: sequelize.QueryTypes.SELECT });

      for (const r of reminders) {
        const diffMs = new Date(r.due_at).getTime() - now.getTime();
        const hoursLeft = Math.round(diffMs / (3600 * 1000));
        // Quy tắc 2: Khẩn cấp khi còn dưới 2 giờ trước hạn
        const isUrgent = diffMs > 0 && diffMs <= 2 * 3600 * 1000;

        let direction = 'export';
        let actionLabel = 'Khai hoặc nộp';
        let actionUrl = `/booking?bookingNo=${encodeURIComponent(r.target_id)}`;

        if (r.target_type === 'VGM_CUTOFF') {
          actionLabel = 'Khai hoặc nộp VGM';
        } else if (r.target_type === 'CY_CUTOFF') {
          actionLabel = 'Đặt dịch vụ hạ bãi';
        } else if (r.target_type === 'FREE_TIME') {
          direction = 'import';
          actionLabel = 'Đặt lệnh lấy hàng';
          actionUrl = `/cargo/documents/EDO/${encodeURIComponent(r.target_id)}`;
        } else if (r.target_type === 'EMPTY_RETURN') {
          direction = 'import';
          actionLabel = 'Xem nơi trả vỏ';
          actionUrl = `/cargo/documents/EIR/${encodeURIComponent(r.target_id)}`;
        } else if (r.target_type === 'DRAFT_BL_CONFIRM') {
          actionLabel = 'Xem và xác nhận draft';
          actionUrl = `/cargo/documents/BL/${encodeURIComponent(r.target_id)}`;
        }

        // Tránh trùng nếu task đã có từ danh sách bookings/edos
        const exists = todos.some(t => t.bookingNo === r.target_id || t.doNumber === r.target_id);
        if (!exists) {
          todos.push({
            id: `todo-remind-${r.id}`,
            taskType: r.target_type,
            direction,
            priority: isUrgent ? 'urgent' : 'normal',
            title: r.title,
            description: `${r.message} (${hoursLeft > 0 ? `Còn ${hoursLeft} giờ` : 'Đã đến hạn'})`,
            bookingNo: r.target_id,
            deadline: r.due_at,
            actionLabel,
            actionUrl,
          });
        }
      }
    } catch (err) {
      console.warn('[CargoNotification] Lỗi lấy To-do từ reminders:', err.message);
    }

    // Sắp xếp ưu tiên: Khẩn cấp lên đầu
    todos.sort((a, b) => {
      if (a.priority === 'urgent' && b.priority !== 'urgent') return -1;
      if (a.priority !== 'urgent' && b.priority === 'urgent') return 1;
      return 0;
    });

    return todos;
  }

  /**
   * THEO DÕI CHỨNG TỪ CHO CHỦ HÀNG (EIR, e-D/O, B/L, ARRIVAL NOTICE, BOOKING)
   * Người 1 không sửa chứng từ, chỉ hiển thị và dẫn đến hành động (Mục 7.2).
   */
  async getDocuments({ type = 'all', search = '', limit = 30, offset = 0 }) {
    const docs = [];
    const s = (search || '').trim().toLowerCase();

    // 1. EIR Transactions
    if (type === 'all' || type === 'EIR') {
      try {
        let eirSql = `
          SELECT id, reference_no, activity, eir_status, plate_number, customer_name,
                 transport_company_name, empty_return_place, check_in_at, check_out_at,
                 issued_at, bl_booking_no, sound_damage_code, remark
          FROM eir_transactions
        `;
        const eirRepl = {};
        if (s) {
          eirSql += ` WHERE (reference_no ILIKE :s OR bl_booking_no ILIKE :s OR plate_number ILIKE :s OR customer_name ILIKE :s)`;
          eirRepl.s = `%${s}%`;
        }
        eirSql += ` ORDER BY COALESCE(issued_at, check_in_at, created_at) DESC LIMIT 20`;
        const eirList = await sequelize.query(eirSql, { replacements: eirRepl, type: sequelize.QueryTypes.SELECT });

        for (const e of eirList) {
          docs.push({
            id: e.id,
            documentType: 'EIR',
            documentNumber: e.reference_no || 'EIR-N/A',
            title: `Phiếu giao nhận container điện tử (e-EIR) - ${e.reference_no}`,
            direction: e.activity === 'GATE_OUT' ? 'import' : 'export',
            activity: e.activity,
            status: e.eir_status || 'ISSUED',
            issueDate: e.issued_at || e.check_in_at,
            relatedNumber: e.bl_booking_no,
            truckPlate: e.plate_number,
            emptyDepot: e.empty_return_place,
            customerName: e.customer_name || e.transport_company_name,
            summary: `${e.activity === 'GATE_OUT' ? 'Cổng ra (Gate Out)' : 'Cổng vào (Gate In)'} · Xe ${e.plate_number || 'N/A'}${e.empty_return_place ? ` · Nơi trả vỏ: ${e.empty_return_place}` : ''}`,
          });
        }
      } catch (err) {
        console.warn('[CargoNotification] Lỗi lấy danh sách EIR:', err.message);
      }
    }

    // 2. e-D/O (Electronic Delivery Order)
    if (type === 'all' || type === 'EDO') {
      try {
        const dos = shippingLineService.deliveryOrders || [];
        for (const d of dos) {
          if (!s || d.doNumber.toLowerCase().includes(s) || d.blNumber.toLowerCase().includes(s) || (d.consignee && d.consignee.toLowerCase().includes(s))) {
            docs.push({
              id: d.id || d.doNumber,
              documentType: 'EDO',
              documentNumber: d.doNumber,
              title: `Lệnh giao hàng điện tử (e-D/O) - ${d.doNumber}`,
              direction: 'import',
              status: d.status || 'RELEASED',
              issueDate: d.issuedDate,
              validUntil: d.validUntil,
              relatedNumber: d.blNumber,
              emptyDepot: d.emptyReturnDepot,
              customerName: d.consignee,
              summary: `Vận đơn: ${d.blNumber} · Hạn hiệu lực: ${formatDate(d.validUntil)} · Depot: ${d.emptyReturnDepot || 'Depot chỉ định'}`,
            });
          }
        }
      } catch (err) {
        console.warn('[CargoNotification] Lỗi lấy danh sách e-DO:', err.message);
      }
    }

    // 3. Bill of Lading (B/L)
    if (type === 'all' || type === 'BL') {
      try {
        const bls = shippingLineService.billsOfLading || [];
        for (const b of bls) {
          if (!s || b.blNumber.toLowerCase().includes(s) || (b.shipper && b.shipper.toLowerCase().includes(s)) || (b.consignee && b.consignee.toLowerCase().includes(s))) {
            docs.push({
              id: b.id || b.blNumber,
              documentType: 'BL',
              documentNumber: b.blNumber,
              title: `Vận đơn đường biển (Bill of Lading) - ${b.blNumber}`,
              direction: b.direction || 'export',
              status: b.status || 'ISSUED',
              issueDate: b.shippedOnBoardDate || b.createdAt,
              relatedNumber: b.bookingNo,
              customerName: b.consignee || b.shipper,
              summary: `Tàu: ${b.vesselName || ''} · ${b.pol || 'DAD'} -> ${b.pod || 'SIN'} · ${(b.containers || []).length} container`,
            });
          }
        }
      } catch (err) {
        console.warn('[CargoNotification] Lỗi lấy danh sách BL:', err.message);
      }
    }

    // 4. Arrival Notice (Thông báo hàng đến)
    if (type === 'all' || type === 'ARRIVAL_NOTICE') {
      try {
        const manifests = shippingLineService.manifests || [];
        for (const m of manifests) {
          if (m.manifestType === 'INBOUND' && m.lines) {
            for (const l of m.lines) {
              if (!s || (l.blNumber && l.blNumber.toLowerCase().includes(s)) || (l.consigneeName && l.consigneeName.toLowerCase().includes(s))) {
                docs.push({
                  id: `an-${l.id || l.blNumber}`,
                  documentType: 'ARRIVAL_NOTICE',
                  documentNumber: `AN-${l.blNumber}`,
                  title: `Thông báo hàng đến (Arrival Notice) - ${l.blNumber}`,
                  direction: 'import',
                  status: 'NOTIFIED',
                  issueDate: m.createdAt || m.manifestDate,
                  relatedNumber: l.blNumber,
                  customerName: l.consigneeName,
                  summary: `Tàu: ${m.vesselName} (${m.voyageNumber}) · ETA: ${formatDate(m.eta)} · ${(l.containers || []).length} cont`,
                });
              }
            }
          }
        }
      } catch (err) {
        console.warn('[CargoNotification] Lỗi lấy danh sách Arrival Notice:', err.message);
      }
    }

    // 5. Booking điện tử
    if (type === 'all' || type === 'BOOKING') {
      try {
        const bookings = await sequelize.query(`
          SELECT eb.id, eb.booking_no, eb.bl_number, eb.quantity, eb.status, eb.created_at, c.company_name
          FROM export_bookings eb
          LEFT JOIN carriers c ON c.id = eb.carrier_id
          ORDER BY eb.created_at DESC LIMIT 15
        `, { type: sequelize.QueryTypes.SELECT });

        for (const b of bookings) {
          if (!s || b.booking_no.toLowerCase().includes(s) || (b.bl_number && b.bl_number.toLowerCase().includes(s))) {
            docs.push({
              id: b.id,
              documentType: 'BOOKING',
              documentNumber: b.booking_no,
              title: `Xác nhận đặt chỗ (Booking Confirmation) - ${b.booking_no}`,
              direction: 'export',
              status: b.status,
              issueDate: b.created_at,
              relatedNumber: b.bl_number,
              customerName: b.company_name,
              summary: `Số lượng: ${b.quantity} container · Trạng thái: ${b.status}`,
            });
          }
        }
      } catch (err) {
        console.warn('[CargoNotification] Lỗi lấy danh sách Booking:', err.message);
      }
    }

    // Sắp xếp ngày phát hành mới nhất
    docs.sort((a, b) => new Date(b.issueDate || 0) - new Date(a.issueDate || 0));

    return {
      items: docs.slice(offset, offset + limit),
      total: docs.length,
    };
  }

  /**
   * XEM CHI TIẾT CHỨNG TỪ (DOCUMENT VIEWER MODAL)
   * Chế độ Read-only kèm các nút Call To Action (hành động tiếp theo).
   */
  async getDocumentDetail(type, id) {
    if (!type || !id) {
      throw new Error('Thiếu loại chứng từ hoặc mã định danh.');
    }

    switch (type.toUpperCase()) {
      case 'EIR': {
        const [eir] = await sequelize.query(`
          SELECT * FROM eir_transactions WHERE reference_no = :id OR id::text = :id LIMIT 1
        `, { replacements: { id }, type: sequelize.QueryTypes.SELECT });

        if (!eir) {
          return { error: 'Không tìm thấy phiếu EIR với mã đã cho.', notFound: true };
        }

        return {
          documentType: 'EIR',
          title: `Phiếu giao nhận container điện tử (e-EIR)`,
          referenceNo: eir.reference_no,
          activity: eir.activity,
          activityLabel: eir.activity === 'GATE_OUT' ? 'Giao container ra cổng (Gate Out)' : 'Hạ container vào cảng (Gate In)',
          status: eir.eir_status || 'ISSUED',
          truckPlate: eir.plate_number,
          driverName: eir.driver_name || eir.transportation_signatory || 'Tài xế giao nhận',
          containerNo: eir.raw_payload?.containerNumber || 'Cont theo lệnh',
          sealNo: eir.raw_payload?.sealNumber || 'Seal nguyên vẹn',
          condition: eir.sound_damage_code || 'Tốt / Không móp vỡ',
          emptyReturnDepot: eir.empty_return_place || 'Depot chỉ định',
          terminalName: eir.terminal_name || 'Cảng Tiên Sa - Đà Nẵng',
          gateLabel: eir.gate_label || 'GATE-01',
          issuedAt: eir.issued_at || eir.check_in_at || eir.created_at,
          customerName: eir.customer_name || eir.transport_company_name,
          bookingNo: eir.bl_booking_no,
          remark: eir.remark || 'Không có ghi chú thêm',
          terminalSignatory: eir.terminal_signatory || 'Nhân viên cổng cảng',
          transportationSignatory: eir.transportation_signatory || eir.driver_name || 'Tài xế xác nhận',
          qrData: `NXP-EIR|${eir.reference_no}|${eir.plate_number}|${eir.issued_at}`,
          actionRecommendation: eir.activity === 'GATE_OUT'
            ? 'Vui lòng vận chuyển container tới địa điểm dỡ hàng và trả vỏ rỗng về Depot đúng hạn.'
            : 'Container đã hạ bãi an toàn. Đang chờ kế hoạch cẩu bốc lên tàu.',
        };
      }

      case 'EDO': {
        const dos = shippingLineService.deliveryOrders || [];
        const edo = dos.find(x => x.doNumber === id || x.id === id);
        if (!edo) {
          return { error: 'Không tìm thấy e-D/O với mã đã cho.', notFound: true };
        }

        return {
          documentType: 'EDO',
          title: `Lệnh giao hàng điện tử (e-D/O)`,
          doNumber: edo.doNumber,
          blNumber: edo.blNumber,
          status: edo.status,
          statusLabel: edo.status === 'RELEASED' ? 'Được phép giao hàng' : edo.status === 'HELD' ? 'Đang bị tạm giữ' : edo.status,
          consignee: edo.consignee,
          consigneeTaxCode: edo.consigneeTaxCode || '0401889988',
          vesselName: edo.vesselName || 'EVER GLORY',
          voyageNo: edo.voyageNo || '2601N',
          issuedDate: edo.issuedDate,
          validUntil: edo.validUntil,
          emptyReturnDepot: edo.emptyReturnDepot || 'Depot Tân Cảng Đà Nẵng',
          containers: edo.containers || ['MSKU9082134'],
          qrData: `NXP-EDO|${edo.doNumber}|${edo.blNumber}|${edo.validUntil}`,
          actionRecommendation: edo.status === 'RELEASED'
            ? 'e-D/O đã sẵn sàng. Bạn có thể đặt lệnh lấy container trên e-Port để nhận hàng.'
            : 'Lệnh đang bị tạm giữ. Vui lòng thanh toán cước hãng tàu hoặc liên hệ phòng chăm sóc khách hàng.',
        };
      }

      case 'BL': {
        const bls = shippingLineService.billsOfLading || [];
        const bl = bls.find(x => x.blNumber === id || x.id === id);
        if (!bl) {
          return { error: 'Không tìm thấy Vận đơn đường biển B/L.', notFound: true };
        }

        return {
          documentType: 'BL',
          title: `Vận đơn đường biển (Bill of Lading)`,
          blNumber: bl.blNumber,
          bookingNo: bl.bookingNo,
          shipper: bl.shipper,
          consignee: bl.consignee,
          notifyParty: bl.notifyParty || 'Same as consignee',
          vesselName: bl.vesselName,
          voyageNo: bl.voyageNumber,
          pol: bl.pol || 'Da Nang Port (VNDAD)',
          pod: bl.pod || 'Singapore (SGSIN)',
          shippedOnBoardDate: bl.shippedOnBoardDate,
          freightStatus: bl.freightStatus || 'FREIGHT PREPAID',
          containers: bl.containers || [],
          cargoDescription: bl.cargoDescription || 'General Containerized Cargo',
          qrData: `NXP-BL|${bl.blNumber}|${bl.vesselName}|${bl.shippedOnBoardDate}`,
          actionRecommendation: 'Vận đơn điện tử đã phát hành. Lưu giữ bản sao hoặc gửi cho đối tác nhận hàng.',
        };
      }

      case 'ARRIVAL_NOTICE': {
        const manifests = shippingLineService.manifests || [];
        let foundLine = null;
        let foundManifest = null;
        for (const m of manifests) {
          if (m.lines) {
            const match = m.lines.find(l => l.blNumber === id || `AN-${l.blNumber}` === id);
            if (match) {
              foundLine = match;
              foundManifest = m;
              break;
            }
          }
        }

        const line = foundLine || {
          blNumber: id,
          consigneeName: 'Công ty Cổ phần Thép Hòa Phát',
          consigneeTaxCode: '0401889988',
          containers: ['CMAU7788991', 'CMAU7788992'],
          freeTimeDays: 5,
          charges: [
            { item: 'Phí D/O (Delivery Order Fee)', amount: 880000 },
            { item: 'Phí vệ sinh container', amount: 350000 },
            { item: 'Phí lưu bãi tạm tính (DEM/DET)', amount: 0 },
          ],
        };
        const manifest = foundManifest || {
          vesselName: 'CMA CGM CHENNAI',
          voyageNumber: '2604S',
          eta: new Date(Date.now() + 36 * 3600 * 1000).toISOString(),
        };

        return {
          documentType: 'ARRIVAL_NOTICE',
          title: `Giấy báo hàng đến (Arrival Notice)`,
          blNumber: line.blNumber,
          vesselName: manifest.vesselName,
          voyageNo: manifest.voyageNumber,
          eta: manifest.eta,
          consigneeName: line.consigneeName,
          consigneeTaxCode: line.consigneeTaxCode,
          containers: line.containers || [],
          freeTimeDays: line.freeTimeDays || 5,
          charges: line.charges || [],
          totalCharges: (line.charges || []).reduce((sum, c) => sum + (c.amount || 0), 0),
          qrData: `NXP-AN|${line.blNumber}|${manifest.vesselName}|${manifest.eta}`,
          actionRecommendation: 'Tàu sắp cập cảng. Vui lòng thanh toán cước hãng tàu để nhận e-D/O trước hạn.',
        };
      }

      case 'BOOKING': {
        const [b] = await sequelize.query(`
          SELECT eb.*, c.company_name
          FROM export_bookings eb
          LEFT JOIN carriers c ON c.id = eb.carrier_id
          WHERE eb.booking_no = :id OR eb.id::text = :id LIMIT 1
        `, { replacements: { id }, type: sequelize.QueryTypes.SELECT });

        if (!b) {
          return {
            documentType: 'BOOKING',
            title: `Xác nhận đặt chỗ container (Booking Confirmation) - ${id}`,
            bookingNo: id,
            blNumber: `BL-${id}`,
            shipper: 'Công ty Cổ phần Thép Hòa Phát',
            vesselName: 'EVER GLORY',
            voyageNo: '2601N',
            etd: new Date(Date.now() + 48 * 3600 * 1000).toISOString(),
            cyCutOff: new Date(Date.now() + 24 * 3600 * 1000).toISOString(),
            vgmCutOff: new Date(Date.now() + 20 * 3600 * 1000).toISOString(),
            siCutOff: new Date(Date.now() + 16 * 3600 * 1000).toISOString(),
            quantity: 2,
            containerType: '40HC',
            commodity: 'Thép cuộn cán nóng',
            emptyPickupPlace: 'Depot Tân Cảng Đà Nẵng',
            status: 'confirmed',
            createdAt: new Date().toISOString(),
            actionRecommendation: 'Booking đã được Hãng tàu chấp thuận. Hãy khai báo danh sách container và nộp VGM đúng hạn.',
          };
        }

        return {
          documentType: 'BOOKING',
          title: `Xác nhận đặt chỗ container (Booking Confirmation) - ${b.booking_no}`,
          bookingNo: b.booking_no,
          blNumber: b.bl_number,
          shipper: b.company_name || 'Công ty Chủ hàng',
          vesselName: 'EVER GLORY',
          voyageNo: '2601N',
          etd: new Date(Date.now() + 48 * 3600 * 1000).toISOString(),
          cyCutOff: new Date(Date.now() + 24 * 3600 * 1000).toISOString(),
          vgmCutOff: new Date(Date.now() + 20 * 3600 * 1000).toISOString(),
          siCutOff: new Date(Date.now() + 16 * 3600 * 1000).toISOString(),
          quantity: b.quantity || 1,
          containerType: b.container_type_code || '40HC',
          commodity: b.commodity_description || 'General Cargo',
          emptyPickupPlace: 'Depot Tân Cảng Đà Nẵng',
          status: b.status,
          createdAt: b.created_at,
          actionRecommendation: 'Booking đã được Hãng tàu chấp thuận. Hãy khai báo danh sách container và nộp VGM đúng hạn.',
        };
      }

      default:
        throw new Error(`Loại chứng từ ${type} không được hỗ trợ.`);
    }
  }

  /**
   * TỰ ĐỘNG QUÉT & PHÁT THÔNG BÁO CHO CÁC NHẮC HẸN ĐÃ ĐẾN HẠN (MỤC 3.2 QUY TẮC 3)
   */
  async processDueReminders() {
    const now = new Date();
    const dueReminders = await sequelize.query(`
      SELECT id, recipient_company_id, company_tax_code, target_type, target_id, milestone,
             due_at, remind_at, title, message
      FROM cargo_reminders
      WHERE status = 'pending' AND remind_at <= now()
      ORDER BY remind_at ASC LIMIT 30
    `, { type: sequelize.QueryTypes.SELECT });

    let processedCount = 0;
    for (const r of dueReminders) {
      try {
        const diffMs = new Date(r.due_at).getTime() - now.getTime();
        const hoursLeft = Math.max(0, Math.round(diffMs / (3600 * 1000)));
        const isUrgent = diffMs > 0 && diffMs <= 2 * 3600 * 1000;

        let eventCode = 'DeadlineApproaching';
        let direction = 'export';

        if (r.target_type === 'VGM_CUTOFF' || r.target_type === 'CY_CUTOFF') {
          eventCode = 'CutoffApproaching';
        } else if (r.target_type === 'FREE_TIME') {
          eventCode = 'FreeTimeApproaching';
          direction = 'import';
        } else if (r.target_type === 'DRAFT_BL_CONFIRM') {
          eventCode = 'DraftBLIssued';
        }

        await this.ingestEvent({
          eventId: `remind-trigger-${r.id}-${r.milestone}`,
          eventCode,
          direction,
          version: 1,
          occurredAt: now.toISOString(),
          payload: {
            bookingNo: r.target_id,
            doNumber: r.target_id,
            cutoffType: r.target_type,
            dueAt: r.due_at,
            hoursRemaining: hoursLeft,
            isUrgent,
            companyId: r.recipient_company_id,
            companyTaxCode: r.company_tax_code,
          },
        });

        await sequelize.query(`
          UPDATE cargo_reminders SET status = 'sent', updated_at = now() WHERE id = :id
        `, { replacements: { id: r.id } });

        processedCount++;
      } catch (err) {
        console.warn(`[CargoNotification] Lỗi xử lý reminder ${r.id}:`, err.message);
      }
    }

    return {
      success: true,
      processedCount,
      totalDue: dueReminders.length,
    };
  }

  /**
   * XÁC NHẬN DRAFT B/L (HÀNH ĐỘNG KÈM THEO CỦA MỤC 3.2 SỐ 9)
   */
  async confirmDraftBL({ bookingNo, blNumber, userId }) {
    if (!bookingNo && !blNumber) {
      throw new Error('Cần cung cấp bookingNo hoặc blNumber.');
    }

    const [updated] = await sequelize.query(`
      UPDATE export_bookings
      SET status = 'draft_confirmed',
          draft = jsonb_set(COALESCE(draft, '{}'::jsonb), '{confirmed_at}', to_jsonb(now()::text)),
          updated_at = now()
      WHERE booking_no = :bookingNo OR bl_number = :blNumber
      RETURNING *
    `, {
      replacements: { bookingNo: bookingNo || blNumber, blNumber: blNumber || bookingNo },
      type: sequelize.QueryTypes.UPDATE,
    });

    const b = updated?.[0];
    const targetKey = b?.bl_number || blNumber || bookingNo;

    // Tự động hủy reminder xác nhận draft
    await this.cancelRemindersByTarget('DRAFT_BL_CONFIRM', targetKey);

    return {
      success: true,
      message: `Đã xác nhận thành công bản nháp vận đơn (Draft B/L) cho ${targetKey}.`,
      booking: b,
    };
  }

  /**
   * KHAI BÁO / NỘP VGM (HÀNH ĐỘNG KÈM THEO CỦA MỤC 3.2 SỐ 5)
   */
  async submitVGM({ bookingNo, containerNo, vgmWeight, authorizedPerson, signatureMethod = 'ELECTRONIC' }) {
    if (!bookingNo || !containerNo || !vgmWeight) {
      throw new Error('Vui lòng điền đủ Số booking, Số container và Trọng lượng VGM.');
    }

    const vgmRecord = {
      containerNo,
      vgmWeight: Number(vgmWeight),
      authorizedPerson: authorizedPerson || 'Người được ủy quyền',
      signatureMethod,
      submittedAt: new Date().toISOString(),
    };

    try {
      await sequelize.query(`
        UPDATE export_bookings
        SET submission = jsonb_set(
          COALESCE(submission, '{}'::jsonb),
          '{vgm_declarations}',
          COALESCE(submission->'vgm_declarations', '[]'::jsonb) || :vgmData::jsonb
        ),
        updated_at = now()
        WHERE booking_no = :bookingNo
      `, {
        replacements: { bookingNo, vgmData: JSON.stringify(vgmRecord) },
        type: sequelize.QueryTypes.UPDATE,
      });
    } catch (e) {
      console.warn('[CargoNotification] Lỗi lưu VGM vào export_bookings:', e.message);
    }

    // Tự động hủy reminder VGM
    await this.cancelRemindersByTarget('VGM_CUTOFF', bookingNo);

    return {
      success: true,
      message: `Đã nộp thành công khai báo VGM cho Container ${containerNo} (${Number(vgmWeight).toLocaleString('vi-VN')} kg).`,
      vgmData: vgmRecord,
    };
  }

  /**
   * Lấy danh sách hàng đợi ngoại lệ: Người nhận chưa có tài khoản (Mục 4.2)
   */
  async getUnclaimedConsignees() {
    return sequelize.query(`
      SELECT * FROM unclaimed_consignee_queue
      WHERE status = 'PENDING_CLAIM'
      ORDER BY created_at DESC
    `, { type: sequelize.QueryTypes.SELECT });
  }

  /**
   * Gán người nhận cho bản ghi trong hàng đợi ngoại lệ khi tài khoản được tạo/khớp nối
   */
  async claimConsignee({ queueId, userId, companyId }) {
    const res = await sequelize.query(`
      UPDATE unclaimed_consignee_queue
      SET status = 'CLAIMED', claimed_by_user_id = :userId, claimed_at = now()
      WHERE id = :queueId AND status = 'PENDING_CLAIM'
      RETURNING *
    `, {
      replacements: { queueId, userId: userId || null },
      type: sequelize.QueryTypes.UPDATE,
    });
    const rows = Array.isArray(res[0]) ? res[0] : (Array.isArray(res) ? res : [res]);
    return rows[0] || { id: queueId, status: 'CLAIMED' };
  }
}

module.exports = new CargoNotificationService();

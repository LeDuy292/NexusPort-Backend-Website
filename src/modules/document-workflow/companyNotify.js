'use strict';

/**
 * Thông báo của Hãng tàu gửi cho Transport company (chủ hàng) khi hoàn thành một chặng chứng từ.
 *
 * Chiều xuất: B/L đã phát hành, pre-alert đã gửi cho đầu nhập.
 * Chiều nhập: hàng sắp về cảng, lệnh giao hàng (D/O) đã phát hành.
 * (Arrival Notice, yêu cầu sửa, LOI, đổi lịch tàu... do importDocuments / postIssuance gửi.)
 *
 * Lỗi gửi thông báo không làm hỏng thao tác nghiệp vụ chính.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

const norm = (v) => String(v || '').replace(/\s+/g, ' ').trim().toLowerCase();
const dateText = (v) => {
  if (!v) return 'chưa xác định';
  const d = new Date(String(v).includes('T') || String(v).includes('Z') ? v : String(v).replace(' ', 'T') + '+07:00');
  if (Number.isNaN(d.getTime())) return String(v);
  const p = Object.fromEntries(new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Ho_Chi_Minh', day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false }).formatToParts(d).map((x) => [x.type, x.value]));
  return p.day + '/' + p.month + '/' + p.year + ' ' + p.hour + ':' + p.minute;
};
const BL_TYPE = { ORIGINAL: 'B/L gốc', Original: 'B/L gốc', TELEX_RELEASE: 'Telex release', TelexRelease: 'Telex release', SEAWAY_BILL: 'Seaway Bill', SeawayBill: 'Seaway Bill' };

/** Tìm Transport company theo tên (hoặc mã số thuế nằm trong chuỗi). Nếu có nhiều dòng trùng thì ưu tiên công ty đã có tài khoản; vẫn mơ hồ thì không chọn. */
async function findCompany(name) {
  const text = norm(name);
  if (!text) return null;
  const rows = await sequelize.query(
    'SELECT c.id, c.company_name, c.tax_code, EXISTS (SELECT 1 FROM carrier_users cu WHERE cu.carrier_id = c.id) AS has_users FROM carriers c',
    { type: QueryTypes.SELECT },
  );
  const pick = (input) => {
    const list = [...new Map(input.map((c) => [c.id, c])).values()]; // bảng có thể lặp dòng cùng id
    if (list.length <= 1) return list[0] || null;
    const withUsers = list.filter((c) => c.has_users);
    return withUsers.length === 1 ? withUsers[0] : null;
  };
  const exact = rows.filter((c) => norm(c.company_name) === text || (c.tax_code && text.includes(String(c.tax_code).toLowerCase())));
  if (exact.length) return pick(exact);
  return pick(rows.filter((c) => norm(c.company_name).length > 5 && (text.includes(norm(c.company_name)) || norm(c.company_name).includes(text))));
}

async function send({ companyId, direction, eventType, priority = 'high', title, message, ref, url, documentType, dedupKey }) {
  if (!companyId) return false;
  try {
    await sequelize.query(
      `INSERT INTO cargo_notifications(recipient_company_id, company_tax_code, recipient_role, event_type, direction, priority, title, message, data,
                                       action_type, action_label, action_url, document_type, document_id, dedup_key)
       SELECT c.id, c.tax_code, 'Transport Company', :eventType, :direction, :priority, :title, :message, jsonb_build_object('ref', CAST(:ref AS text)),
              'view_details', 'Xem chi tiết', :url, :documentType, CAST(:ref AS text), :dedupKey
         FROM carriers c WHERE c.id = :companyId AND NOT EXISTS (SELECT 1 FROM cargo_notifications n WHERE n.dedup_key = :dedupKey) LIMIT 1`,
      { replacements: { companyId, direction, eventType, priority, title, message, ref, url, documentType, dedupKey } },
    );
    return true;
  } catch (err) {
    console.warn('[CompanyNotify] Không gửi được thông báo:', err.message);
    return false;
  }
}

const containerLines = (containers) => (containers || []).slice(0, 12)
  .map((c) => `${c.containerNumber || c.containerNo}${c.sealNumber || c.sealNo ? ` (seal ${c.sealNumber || c.sealNo})` : ''}`).join(', ')
  + ((containers || []).length > 12 ? `, … (${containers.length} container)` : '');

/** Chiều xuất: B/L đã phát hành, gửi cho chủ hàng (shipper) và báo pre-alert đã chuyển đầu nhập. */
async function notifyBlIssuedExport(bl, { companyId } = {}) {
  const company = companyId ? { id: companyId } : await findCompany(bl.shipper);
  if (!company) return false;
  const lines = [
    `Hãng tàu đã phát hành vận đơn ${bl.blNumber}${bl.bookingNo ? ` (booking ${bl.bookingNo})` : ''}.`,
    `Tàu/chuyến: ${bl.vesselName || '—'} / ${bl.voyageNumber || '—'}. Cảng bốc → cảng dỡ: ${bl.pol || '—'} → ${bl.pod || '—'}. ETD: ${dateText(bl.etd)}.`,
    `Người nhận: ${bl.consignee || '—'}. Hình thức B/L: ${BL_TYPE[bl.blType] || bl.blType || '—'}. Cước: ${bl.freightStatus === 'COLLECT' || bl.freightTerms === 'Collect' ? 'trả sau (Collect)' : 'trả trước (Prepaid)'}.`,
    (bl.containers || []).length ? `Container (${bl.containers.length}): ${containerLines(bl.containers)}.` : null,
    'Pre-alert đã được gửi cho đại lý đầu nhập. Nếu thông tin trên B/L chưa đúng, vui lòng liên hệ hãng tàu ngay.',
  ].filter(Boolean);
  return send({
    companyId: company.id, direction: 'export', eventType: 'BlIssued', title: `B/L ${bl.blNumber} đã được phát hành`, message: lines.join('\n'),
    ref: bl.blNumber, url: `/booking?bookingNo=${encodeURIComponent(bl.bookingNo || bl.blNumber)}`, documentType: 'BL', dedupKey: `bl-issued-${bl.blNumber}`,
  });
}

/** Chiều nhập: hàng sắp về cảng, gửi cho người nhận hàng khi hồ sơ nhập được mở. */
async function notifyCargoArriving(bl, { companyId } = {}) {
  const company = companyId ? { id: companyId } : await findCompany(bl.consignee);
  if (!company) return false;
  const lines = [
    `Lô hàng theo vận đơn ${bl.blNumber} dự kiến về cảng Tiên Sa.`,
    `Tàu/chuyến: ${bl.vesselName || '—'} / ${bl.voyageNumber || '—'}. ETA: ${dateText(bl.eta)}.`,
    `Người gửi: ${bl.shipper || '—'}. Hình thức B/L: ${BL_TYPE[bl.blType] || bl.blType || '—'}.`,
    'Hãng tàu sẽ gửi Arrival Notice kèm phí, free time và chứng từ cần xuất trình. Vui lòng chuẩn bị thông tin xuất hóa đơn và người liên hệ.',
  ];
  return send({
    companyId: company.id, direction: 'import', eventType: 'CargoArriving', priority: 'normal', title: `Hàng nhập ${bl.blNumber} sắp về cảng`, message: lines.join('\n'),
    ref: bl.blNumber, url: `/import-documents?bl=${encodeURIComponent(bl.blNumber)}`, documentType: 'BL', dedupKey: `arriving-${bl.blNumber}`,
  });
}

/** Chiều nhập: D/O đã phát hành, người nhận có thể đặt lịch lấy container. */
async function notifyDoIssued({ blNumber, companyId, edo, containers, stockLocation }) {
  const lines = [
    `Hãng tàu đã phát hành lệnh giao hàng (D/O) ${edo?.edoNumber || ''} cho vận đơn ${blNumber}.`,
    edo?.validUntil ? `Hiệu lực đến: ${dateText(edo.validUntil)}.` : null,
    edo?.freeTimeDays ? `Free time: ${edo.freeTimeDays} ngày.` : null,
    stockLocation ? `Nơi lấy container: ${stockLocation}.` : null,
    (containers || []).length ? `Container (${containers.length}): ${containerLines(containers)}.` : null,
    'Bạn có thể đặt lịch lấy container tại cảng. Mã PIN nhận hàng xem trong hồ sơ trên hệ thống.',
  ].filter(Boolean);
  return send({
    companyId, direction: 'import', eventType: 'DoIssued', title: `D/O cho vận đơn ${blNumber} đã được phát hành`, message: lines.join('\n'),
    ref: blNumber, url: `/import-documents?bl=${encodeURIComponent(blNumber)}`, documentType: 'DO', dedupKey: `do-issued-${blNumber}-${edo?.edoNumber || ''}`,
  });
}

module.exports = { findCompany, notifyBlIssuedExport, notifyCargoArriving, notifyDoIssued };

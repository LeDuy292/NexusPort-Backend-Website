'use strict';

/**
 * Tổng quan hoạt động Hãng tàu: toàn bộ số liệu tính từ DB, chỉ lấy dữ liệu của hãng đang đăng nhập
 * (mọi tài khoản cùng một hãng trong shipping_line_accounts dùng chung dữ liệu). Administrator thấy tất cả.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

const PEERS = `(SELECT user_id FROM shipping_line_accounts
                 WHERE shipping_line_id = (SELECT shipping_line_id FROM shipping_line_accounts WHERE user_id = :me)
                UNION SELECT :me::uuid)`;
const scope = (col) => `(:all OR ${col} IN ${PEERS})`;

const run = (sql, replacements) => sequelize.query(sql, { replacements, type: QueryTypes.SELECT });
const countBy = (rows, key = 'status') => Object.fromEntries(rows.map((r) => [r[key] ?? 'UNKNOWN', Number(r.n)]));

async function getDashboard(actor) {
  const r = { me: actor.id, all: actor.role === 'Administrator' };

  const [voyageStatus, bookingStatus, manifestRows, importStatus, requestRows, lineRow] = await Promise.all([
    run(`SELECT status, count(*)::int n FROM export_voyages v WHERE ${scope('v.line_user_id')} GROUP BY 1`, r),
    run(`SELECT b.status, count(*)::int n FROM export_bookings b WHERE ${scope('b.line_user_id')} GROUP BY 1`, r),
    run(`SELECT upper(m.payload->>'direction') AS direction, m.payload->>'status' AS status, count(*)::int n
           FROM shipping_manifests m WHERE ${scope('m.owner_id')} GROUP BY 1, 2`, r),
    run(`SELECT s.status, count(*)::int n FROM import_shipments s WHERE ${scope('s.line_user_id')} GROUP BY 1`, r),
    run(`SELECT q.request_type AS status, count(*)::int n FROM bl_post_issuance_requests q
           JOIN import_shipments s ON s.id = q.import_shipment_id
          WHERE q.status = 'PENDING' AND ${scope('s.line_user_id')} GROUP BY 1`, r),
    run(`SELECT l.short_name, l.full_name FROM shipping_line_accounts a JOIN shipping_lines l ON l.id = a.shipping_line_id
          WHERE a.user_id = :me LIMIT 1`, r),
  ]);

  // Chuyến tàu sắp tới và mốc hạn chót trong 48 giờ tới
  const voyages = await run(
    `SELECT v.id, v.vessel_name AS "vesselName", v.voyage_number AS "voyageNumber", v.port_of_loading AS pol,
            v.port_of_discharge AS pod, v.etd, v.eta, v.cy_cutoff AS "cyCutoff", v.vgm_cutoff AS "vgmCutoff",
            v.si_cutoff AS "siCutoff", v.status,
            (SELECT count(*)::int FROM export_bookings b WHERE b.voyage_id = v.id) AS bookings,
            (SELECT count(*)::int FROM export_bookings b WHERE b.voyage_id = v.id AND b.status = 'Pending') AS "bookingsPending",
            (SELECT count(*)::int FROM export_booking_containers c JOIN export_bookings b ON b.id = c.export_booking_id WHERE b.voyage_id = v.id) AS containers
       FROM export_voyages v WHERE ${scope('v.line_user_id')} AND v.status NOT IN ('Departed', 'Cancelled')
      ORDER BY v.etd ASC LIMIT 8`, r);

  // Vận đơn trong manifest: đã phát hành hay chưa (so với bills_of_lading cùng chiều)
  const [bills] = await run(
    `SELECT count(*)::int total,
            count(*) FILTER (WHERE bl.id IS NOT NULL)::int issued,
            count(*) FILTER (WHERE bl.id IS NULL)::int "notIssued"
       FROM shipping_manifest_bills mb
       JOIN shipping_manifests m ON m.id = mb.manifest_id
       LEFT JOIN bills_of_lading bl ON upper(bl.bl_number) = upper(mb.bl_number) AND lower(bl.direction) = lower(m.payload->>'direction')
      WHERE ${scope('mb.owner_id')}`, r);

  // Hồ sơ hàng nhập cần hãng tàu xử lý
  const [importDue] = await run(
    `SELECT count(*) FILTER (WHERE s.status = 'NOTICE_SENT' AND s.notice_deadline < now())::int "noticeOverdue",
            count(*) FILTER (WHERE s.status = 'AMENDMENT_REQUESTED')::int "amendments",
            count(*) FILTER (WHERE s.status = 'MISMATCH')::int mismatches,
            count(*) FILTER (WHERE s.status IN ('CONFIRMED', 'RELEASE_VERIFIED'))::int "awaitingDo"
       FROM import_shipments s WHERE ${scope('s.line_user_id')}`, r);

  // B/L gốc (LOI) quá hạn nộp
  const [loi] = await run(
    `SELECT count(*) FILTER (WHERE q.original_due_at < now() AND q.original_returned_at IS NULL)::int overdue,
            count(*) FILTER (WHERE q.original_returned_at IS NULL)::int outstanding
       FROM bl_post_issuance_requests q JOIN import_shipments s ON s.id = q.import_shipment_id
      WHERE q.request_type = 'LOI_RELEASE' AND q.status = 'APPROVED' AND ${scope('s.line_user_id')}`, r);

  // e-D/O của các vận đơn nhập thuộc hãng
  const [edo] = await run(
    `SELECT count(*)::int total,
            count(*) FILTER (WHERE d.status = 'RELEASED' AND d.valid_until >= now())::int active,
            count(*) FILTER (WHERE d.status = 'RELEASED' AND d.valid_until >= now() AND d.valid_until < now() + interval '48 hours')::int "expiringSoon",
            count(*) FILTER (WHERE d.status = 'RELEASED' AND d.valid_until < now())::int expired,
            count(*) FILTER (WHERE d.status = 'HELD')::int held
       FROM delivery_orders d
      WHERE :all OR EXISTS (SELECT 1 FROM import_shipments s WHERE s.do_number = d.do_number AND s.line_user_id IN ${PEERS})`, r);

  // Hoạt động gần đây từ nhật ký hồ sơ
  const activity = await run(
    `SELECT e.id, e.direction, e.file_ref AS "fileRef", e.document_type AS "documentType", e.action, e.to_status AS "toStatus",
            e.actor_username AS actor, e.note, e.created_at AS "createdAt"
       FROM shipment_document_events e
      WHERE :all OR e.actor_user_id IN ${PEERS}
         OR e.file_ref IN (SELECT mb.bl_number FROM shipping_manifest_bills mb WHERE mb.owner_id IN ${PEERS})
         OR e.file_ref IN (SELECT s.bl_number FROM import_shipments s WHERE s.line_user_id IN ${PEERS})
      ORDER BY e.created_at DESC LIMIT 12`, r);

  const manifest = { export: {}, import: {}, total: 0 };
  for (const m of manifestRows) {
    const side = m.direction === 'IMPORT' ? 'import' : 'export';
    manifest[side][m.status || 'UNKNOWN'] = Number(m.n);
    manifest.total += Number(m.n);
  }

  const voyageCount = countBy(voyageStatus);
  const bookingCount = countBy(bookingStatus);
  const importCount = countBy(importStatus);
  const requests = countBy(requestRows);
  const sum = (o) => Object.values(o).reduce((a, b) => a + b, 0);

  // Việc cần xử lý, mỗi việc có đường dẫn tới đúng trang nghiệp vụ
  const alerts = [];
  const soon = new Date(Date.now() + 48 * 3600 * 1000);
  for (const v of voyages) {
    for (const [label, at] of [['CY cut-off', v.cyCutoff], ['VGM cut-off', v.vgmCutoff], ['SI cut-off', v.siCutoff]]) {
      if (at && new Date(at) > new Date() && new Date(at) <= soon) {
        alerts.push({ id: `cut-${v.id}-${label}`, type: 'WARNING', title: `${label} chuyến ${v.voyageNumber} (${v.vesselName}) trong 48 giờ tới`,
          description: `Hạn: ${new Date(at).toISOString()}. Còn ${v.bookingsPending} booking chưa khai container.`, targetPath: '/shipping-line/voyages' });
      }
    }
  }
  if (bills?.notIssued > 0) alerts.push({ id: 'bl-not-issued', type: 'WARNING', title: `${bills.notIssued} vận đơn trong bản khai chưa phát hành`, description: 'Phát hành B/L ngay trong bản khai hàng hóa.', targetPath: '/shipping-line/manifest' });
  if (importDue?.noticeOverdue > 0) alerts.push({ id: 'notice-overdue', type: 'ERROR', title: `${importDue.noticeOverdue} thông báo hàng đến quá hạn xác nhận`, description: 'Chủ hàng chưa xác nhận Arrival Notice đúng hạn.', targetPath: '/shipping-line/import-documents' });
  if (importDue?.amendments > 0) alerts.push({ id: 'amend', type: 'WARNING', title: `${importDue.amendments} yêu cầu chỉnh sửa thông tin từ chủ hàng`, description: 'Cần duyệt hoặc từ chối chỉnh sửa.', targetPath: '/shipping-line/import-documents' });
  if (importDue?.mismatches > 0) alerts.push({ id: 'mismatch', type: 'ERROR', title: `${importDue.mismatches} hồ sơ nhập lệch thông tin so với pre-alert`, description: 'Đối chiếu và gửi thông báo cho chủ hàng.', targetPath: '/shipping-line/import-documents' });
  if (importDue?.awaitingDo > 0) alerts.push({ id: 'await-do', type: 'INFO', title: `${importDue.awaitingDo} hồ sơ đủ điều kiện, chờ phát hành D/O`, description: 'Kiểm tra giải phóng hàng rồi phát hành lệnh giao hàng.', targetPath: '/shipping-line/import-documents' });
  if (sum(requests) > 0) alerts.push({ id: 'post-issuance', type: 'WARNING', title: `${sum(requests)} yêu cầu sau phát hành chờ duyệt`, description: 'LOI, đổi consignee, đổi POD hoặc đổi lịch tàu.', targetPath: '/shipping-line/import-documents' });
  if (loi?.overdue > 0) alerts.push({ id: 'loi-overdue', type: 'ERROR', title: `${loi.overdue} bộ B/L gốc quá hạn nộp lại sau LOI`, description: 'Liên hệ chủ hàng thu hồi B/L gốc.', targetPath: '/shipping-line/import-documents' });
  if (edo?.expiringSoon > 0) alerts.push({ id: 'edo-soon', type: 'WARNING', title: `${edo.expiringSoon} lệnh giao hàng sắp hết hạn trong 48 giờ`, description: 'Chủ hàng cần lấy hàng hoặc gia hạn.', targetPath: '/shipping-line/delivery-orders' });
  if (edo?.held > 0) alerts.push({ id: 'edo-held', type: 'INFO', title: `${edo.held} lệnh giao hàng đang bị giữ`, description: 'Kiểm tra lý do giữ hàng.', targetPath: '/shipping-line/delivery-orders' });

  return {
    line: lineRow[0] || null,
    generatedAt: new Date().toISOString(),
    export: {
      voyages: { total: sum(voyageCount), byStatus: voyageCount },
      bookings: { total: sum(bookingCount), byStatus: bookingCount },
      manifests: manifest,
      bills: bills || { total: 0, issued: 0, notIssued: 0 },
      upcomingVoyages: voyages,
    },
    import: {
      shipments: { total: sum(importCount), byStatus: importCount },
      due: importDue,
      postIssuanceRequests: { total: sum(requests), byType: requests },
      loi,
      edo,
    },
    alerts,
    activity,
  };
}

module.exports = { getDashboard };

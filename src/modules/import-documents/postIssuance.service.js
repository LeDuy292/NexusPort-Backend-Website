'use strict';

/**
 * Ngoại lệ sau khi B/L đã phát hành, trên hồ sơ hàng nhập:
 *  - LOI_RELEASE: Transport company xin nhận hàng không cần B/L gốc bằng thư bảo lãnh (LOI)
 *  - CHANGE_CONSIGNEE / CHANGE_POD: đổi người nhận / cảng dỡ, cần Hãng tàu duyệt và thu phí
 *  - SCHEDULE_CHANGE: Hãng tàu đổi lịch tàu (rớt tàu, đổi chuyến) và báo cho người nhận
 * Mọi bước ghi vào shipment_document_events; chỉ thực hiện được khi chưa phát hành D/O.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const repo = require('../shipping-line/shippingLine.repository');
const { recordDocumentEvent } = require('../document-workflow/documentEvents');
const imports = require('./importDocuments.service');

const { httpError, needLine, needCompany, loadFile, notifyCompany, notifyLine, STATUS } = imports;
const json = (v) => JSON.stringify(v);
const LOI_RETURN_DAYS = Math.max(1, Number(process.env.LOI_ORIGINAL_RETURN_DAYS) || 30);
const REMINDER_DAYS = 3;
const COMPANY_TYPES = ['LOI_RELEASE', 'CHANGE_CONSIGNEE', 'CHANGE_POD'];
const OUR_PORT = /ti[êe]n\s*sa|vnda|\bdad\b|da\s*nang|đà\s*nẵng/i;
const urlOk = (u) => /^(https?:\/\/|\/uploads\/)/i.test(String(u || ''));
const text = (v) => String(v ?? '').trim();

function fromRow(r) {
  return {
    id: r.id, importShipmentId: r.import_shipment_id, blNumber: r.bl_number, type: r.request_type, status: r.status,
    initiatedBy: r.initiated_by, payload: r.payload, feeVnd: Number(r.fee_vnd), feePaid: r.fee_paid,
    resolutionNote: r.resolution_note, resolvedAt: r.resolved_at, createdAt: r.created_at,
    originalDueAt: r.original_due_at, originalReturnedAt: r.original_returned_at,
    originalOverdue: Boolean(r.original_due_at && !r.original_returned_at && new Date(r.original_due_at) < new Date()),
  };
}

async function listForFile(importId, actor) {
  const row = await loadFile(importId, actor);
  const rows = await sequelize.query(
    'SELECT * FROM bl_post_issuance_requests WHERE import_shipment_id = :id ORDER BY created_at DESC',
    { replacements: { id: row.id }, type: QueryTypes.SELECT },
  );
  return rows.map(fromRow);
}

/** Transport company gửi yêu cầu LOI / đổi consignee / đổi cảng dỡ. */
async function create(importId, input = {}, actor) {
  needCompany(actor);
  const type = input.type;
  if (!COMPANY_TYPES.includes(type)) throw httpError(400, 'Loại yêu cầu không hợp lệ.');
  const row = await loadFile(importId, actor);
  if (row.status === STATUS.DO_ISSUED) throw httpError(409, 'Đã phát hành D/O nên không thể yêu cầu thay đổi sau phát hành.');
  const reason = text(input.reason);
  if (!reason) throw httpError(400, 'Cần nêu lý do.');
  const payload = { reason };

  if (type === 'LOI_RELEASE') {
    if (row.bl_type !== 'Original') throw httpError(409, 'LOI chỉ áp dụng cho B/L gốc chưa có bản gốc để nộp.');
    if (!urlOk(input.letterUrl)) throw httpError(400, 'Cần tải lên thư bảo lãnh (LOI) có chữ ký, đóng dấu.');
    payload.letterUrl = input.letterUrl;
  } else if (type === 'CHANGE_CONSIGNEE') {
    payload.newConsignee = text(input.newConsignee);
    if (!payload.newConsignee) throw httpError(400, 'Cần nêu tên consignee mới.');
    if (!urlOk(input.letterUrl)) throw httpError(400, 'Cần văn bản đồng ý của shipper về việc đổi consignee.');
    payload.letterUrl = input.letterUrl;
    payload.oldConsignee = row.consignee;
  } else {
    payload.newPod = text(input.newPod);
    if (!payload.newPod) throw httpError(400, 'Cần nêu cảng dỡ mới.');
    if (!OUR_PORT.test(payload.newPod)) throw httpError(409, 'Cảng dỡ mới nằm ngoài cảng này: hồ sơ phải do đại lý đầu nhập tại cảng mới xử lý.');
    if (!urlOk(input.letterUrl)) throw httpError(400, 'Cần văn bản đồng ý của shipper về việc đổi cảng dỡ.');
    payload.letterUrl = input.letterUrl;
    payload.oldPod = row.port_of_discharge;
  }

  let created;
  try {
    [created] = await sequelize.query(
      `INSERT INTO bl_post_issuance_requests(import_shipment_id, bl_number, request_type, initiated_by, requested_by, payload)
       VALUES (:id, :bl, :type, 'COMPANY', :by, CAST(:payload AS jsonb)) RETURNING *`,
      { replacements: { id: row.id, bl: row.bl_number, type, by: actor.id, payload: json(payload) }, type: QueryTypes.SELECT },
    );
  } catch (err) {
    if (err.original?.code === '23505') throw httpError(409, 'Đã có yêu cầu cùng loại đang chờ Hãng tàu xử lý.');
    throw err;
  }
  await recordDocumentEvent({
    direction: 'IMPORT', fileRef: row.bl_number, documentType: 'BL', action: `${type}_REQUESTED`, actor, note: reason,
    fieldChanges: type === 'CHANGE_CONSIGNEE' ? [{ field: 'consignee', old: row.consignee, new: payload.newConsignee }]
      : type === 'CHANGE_POD' ? [{ field: 'portOfDischarge', old: row.port_of_discharge, new: payload.newPod }] : undefined,
  });
  const typeText = { LOI_RELEASE: 'nhận hàng bằng thư bảo lãnh (LOI)', CHANGE_CONSIGNEE: 'đổi người nhận', CHANGE_POD: 'đổi cảng dỡ' }[type] || type;
  await notifyLine(row, `BlRequest${type}`, `Yêu cầu ${typeText} [${row.bl_number}]`, `Lý do: ${reason}`, `bl-req-${created.id}`);
  return fromRow(created);
}

/** Transport company rút yêu cầu khi Hãng tàu chưa xử lý. */
async function cancel(requestId, actor) {
  needCompany(actor);
  const req = await loadRequest(requestId);
  await loadFile(req.import_shipment_id, actor);
  if (req.status !== 'PENDING') throw httpError(409, 'Yêu cầu đã được xử lý.');
  await sequelize.query(`UPDATE bl_post_issuance_requests SET status = 'CANCELLED', updated_at = now() WHERE id = :id`, { replacements: { id: requestId } });
  await recordDocumentEvent({ direction: 'IMPORT', fileRef: req.bl_number, documentType: 'BL', action: `${req.request_type}_CANCELLED`, actor });
  return { id: requestId, status: 'CANCELLED' };
}

async function loadRequest(id) {
  const [req] = await sequelize.query('SELECT * FROM bl_post_issuance_requests WHERE id = :id', { replacements: { id }, type: QueryTypes.SELECT });
  if (!req) throw httpError(404, 'Không tìm thấy yêu cầu.');
  return req;
}

/** Hãng tàu duyệt hoặc từ chối. Có phí thì phải đã thu phí trước khi áp dụng thay đổi. */
async function resolve(requestId, { approve, note, feeVnd, feePaid } = {}, actor) {
  needLine(actor);
  const t = await sequelize.transaction();
  let req; let row; let outcome;
  try {
    const [locked] = await sequelize.query('SELECT * FROM bl_post_issuance_requests WHERE id = :id FOR UPDATE', { replacements: { id: requestId }, type: QueryTypes.SELECT, transaction: t });
    if (!locked) throw httpError(404, 'Không tìm thấy yêu cầu.');
    req = locked;
    row = await loadFile(req.import_shipment_id, actor, { lock: true, transaction: t });
    if (req.status !== 'PENDING') throw httpError(409, 'Yêu cầu đã được xử lý.');
    if (row.status === STATUS.DO_ISSUED) throw httpError(409, 'Đã phát hành D/O nên không thể thay đổi sau phát hành.');

    if (!approve) {
      if (!text(note)) throw httpError(400, 'Cần nêu lý do từ chối.');
      await sequelize.query(
        `UPDATE bl_post_issuance_requests SET status = 'REJECTED', resolution_note = :n, resolved_by = :u, resolved_at = now(), updated_at = now() WHERE id = :id`,
        { replacements: { id: requestId, n: text(note), u: actor.id }, transaction: t },
      );
      outcome = 'REJECTED';
    } else {
      const fee = Number(feeVnd || 0);
      if (!(fee >= 0)) throw httpError(400, 'Phí không hợp lệ.');
      if (fee > 0 && feePaid !== true) throw httpError(409, 'Cần thu phí xử lý trước khi áp dụng thay đổi.');
      await sequelize.query(
        `UPDATE bl_post_issuance_requests SET status = 'APPROVED', fee_vnd = :fee, fee_paid = :paid, resolution_note = :n, resolved_by = :u, resolved_at = now(), updated_at = now() WHERE id = :id`,
        { replacements: { id: requestId, fee, paid: fee > 0, n: text(note) || null, u: actor.id }, transaction: t },
      );
      await applyChange(req, row, actor, t);
      if (req.request_type === 'LOI_RELEASE') {
        await sequelize.query(`UPDATE bl_post_issuance_requests SET original_due_at = now() + (:days * interval '1 day') WHERE id = :id`, { replacements: { id: requestId, days: LOI_RETURN_DAYS }, transaction: t });
      }
      outcome = 'APPROVED';
    }
    await t.commit();
  } catch (err) {
    await t.rollback().catch(() => {});
    throw err;
  }

  await recordDocumentEvent({
    direction: 'IMPORT', fileRef: req.bl_number, documentType: 'BL', action: `${req.request_type}_${outcome}`, actor, note: text(note) || undefined,
  });
  await notifyCompany(row.consignee_company_id, req.bl_number, `BlRequest${outcome}`,
    `Yêu cầu ${label(req.request_type)} [${req.bl_number}] ${outcome === 'APPROVED' ? 'đã được chấp nhận' : 'bị từ chối'}`,
    text(note) || (outcome === 'APPROVED' ? 'Hãng tàu đã cập nhật hồ sơ theo yêu cầu.' : 'Hãng tàu từ chối yêu cầu.'), `bl-req-${requestId}-${outcome}`);
  const [updated] = await sequelize.query('SELECT * FROM bl_post_issuance_requests WHERE id = :id', { replacements: { id: requestId }, type: QueryTypes.SELECT });
  return fromRow(updated);
}

const label = (type) => ({ LOI_RELEASE: 'LOI', CHANGE_CONSIGNEE: 'đổi consignee', CHANGE_POD: 'đổi cảng dỡ', SCHEDULE_CHANGE: 'đổi lịch tàu' }[type] || type);

/** Áp dụng thay đổi trong cùng transaction với việc duyệt; phần B/L gốc cập nhật sau cùng. */
async function applyChange(req, row, actor, t) {
  const p = req.payload || {};
  const reset = `status = 'RECEIVED', reconciled_at = NULL, mismatch = NULL, notice = NULL, notice_deadline = NULL,
                 invoice_info = NULL, contact_info = NULL, amendment_request = NULL, release_check = NULL`;
  const bl = await repo.findBl(req.bl_number);
  if (req.request_type === 'LOI_RELEASE') {
    // Nhận hàng bằng LOI: coi như được giải phóng không cần bản gốc, người nhận chịu trách nhiệm theo thư bảo lãnh
    if (bl) await repo.updateBl(bl.id, { status: 'TELEX_RELEASED' });
    return;
  }
  if (req.request_type === 'CHANGE_CONSIGNEE') {
    await sequelize.query(
      `UPDATE import_shipments SET consignee = :v, consignee_company_id = NULL, authorization_file_url = NULL, ${reset}, version = version + 1, updated_at = now() WHERE id = :id`,
      { replacements: { id: row.id, v: p.newConsignee }, transaction: t },
    );
    if (bl) await repo.updateBl(bl.id, { consignee: p.newConsignee });
    return;
  }
  if (req.request_type === 'CHANGE_POD') {
    await sequelize.query(
      `UPDATE import_shipments SET port_of_discharge = :v, ${reset}, version = version + 1, updated_at = now() WHERE id = :id`,
      { replacements: { id: row.id, v: p.newPod }, transaction: t },
    );
    if (bl) await repo.updateBl(bl.id, { pod: p.newPod });
  }
}

/** Hãng tàu đổi lịch tàu (rớt tàu/đổi chuyến): cập nhật hồ sơ, gia hạn Arrival Notice nếu đã gửi và báo cho người nhận. */
async function changeSchedule(importId, input = {}, actor) {
  needLine(actor);
  const reason = text(input.reason);
  if (!reason) throw httpError(400, 'Cần nêu lý do đổi lịch.');
  const eta = input.eta ? new Date(input.eta) : null;
  if (!eta || Number.isNaN(eta.getTime())) throw httpError(400, 'Cần nêu ETA mới hợp lệ.');
  const vessel = text(input.vesselName) || null;
  const voyage = text(input.voyageNumber) || null;

  const t = await sequelize.transaction();
  let row;
  let nextNotice = null;
  try {
    row = await loadFile(importId, actor, { lock: true, transaction: t });
    if (row.status === STATUS.DO_ISSUED) throw httpError(409, 'Đã phát hành D/O nên không thể đổi lịch tàu.');
    const sets = ['eta = :eta', 'vessel_name = COALESCE(:vessel, vessel_name)', 'voyage_number = COALESCE(:voyage, voyage_number)'];
    const replacements = { id: row.id, eta, vessel, voyage };
    if (row.status === STATUS.NOTICE_SENT && row.notice) {
      const deadline = imports.noticeDeadline(eta);
      const updated = { ...row, eta, vessel_name: vessel || row.vessel_name, voyage_number: voyage || row.voyage_number };
      nextNotice = row.notice_version + 1;
      const notice = imports.buildNotice(updated, nextNotice, {
        fees: row.notice.fees, stockLocation: row.notice.stockLocation, freeTime: row.notice.freeTime, requiredDocuments: row.notice.requiredDocuments,
      }, deadline);
      sets.push('notice = CAST(:notice AS jsonb)', 'notice_version = :nv', 'notice_deadline = :deadline');
      Object.assign(replacements, { notice: json(notice), nv: nextNotice, deadline });
    }
    await sequelize.query(`UPDATE import_shipments SET ${sets.join(', ')}, version = version + 1, updated_at = now() WHERE id = :id`, { replacements, transaction: t });
    await sequelize.query(
      `INSERT INTO bl_post_issuance_requests(import_shipment_id, bl_number, request_type, status, initiated_by, requested_by, payload, resolved_by, resolved_at)
       VALUES (:id, :bl, 'SCHEDULE_CHANGE', 'APPROVED', 'LINE', :u, CAST(:payload AS jsonb), :u, now())`,
      { replacements: { id: row.id, bl: row.bl_number, u: actor.id, payload: json({ reason, vesselName: vessel, voyageNumber: voyage, eta, oldEta: row.eta, oldVessel: row.vessel_name, oldVoyage: row.voyage_number }) }, transaction: t },
    );
    await t.commit();
  } catch (err) {
    await t.rollback().catch(() => {});
    throw err;
  }
  const bl = await repo.findBl(row.bl_number);
  if (bl) await repo.updateBl(bl.id, { ...(vessel ? { vesselName: vessel } : {}), ...(voyage ? { voyageNumber: voyage } : {}) });
  await recordDocumentEvent({
    direction: 'IMPORT', fileRef: row.bl_number, documentType: 'BL', action: 'SCHEDULE_CHANGED', actor, note: reason,
    fieldChanges: [
      { field: 'eta', old: row.eta, new: eta.toISOString() },
      ...(vessel && vessel !== row.vessel_name ? [{ field: 'vesselName', old: row.vessel_name, new: vessel }] : []),
      ...(voyage && voyage !== row.voyage_number ? [{ field: 'voyageNumber', old: row.voyage_number, new: voyage }] : []),
    ],
  });
  await notifyCompany(row.consignee_company_id, row.bl_number, 'ScheduleChanged', `Lịch tàu của vận đơn [${row.bl_number}] đã thay đổi`,
    `ETA mới ${eta.toLocaleString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' })}. Lý do: ${reason}${nextNotice ? '. Arrival Notice đã được cập nhật, vui lòng xác nhận lại.' : ''}`,
    `sched-${row.bl_number}-${eta.getTime()}`);
  return imports.get(importId, actor);
}

/** Hãng tàu xác nhận đã nhận lại B/L gốc cho vận đơn đã nhận hàng bằng LOI. */
async function confirmOriginalReturned(requestId, actor) {
  needLine(actor);
  const req = await loadRequest(requestId);
  await loadFile(req.import_shipment_id, actor);
  if (req.request_type !== 'LOI_RELEASE' || req.status !== 'APPROVED') throw httpError(409, 'Chỉ áp dụng cho LOI đã được duyệt.');
  if (req.original_returned_at) throw httpError(409, 'Đã xác nhận nhận lại B/L gốc.');
  await sequelize.query('UPDATE bl_post_issuance_requests SET original_returned_at = now(), updated_at = now() WHERE id = :id', { replacements: { id: requestId } });
  const bl = await repo.findBl(req.bl_number);
  if (bl) await repo.updateBl(bl.id, { status: 'SURRENDERED' });
  await recordDocumentEvent({ direction: 'IMPORT', fileRef: req.bl_number, documentType: 'BL', action: 'LOI_ORIGINAL_RETURNED', actor, note: 'Đã nhận lại B/L gốc, hoàn tất nghĩa vụ của thư bảo lãnh (LOI).' });
  const [updated] = await sequelize.query('SELECT * FROM bl_post_issuance_requests WHERE id = :id', { replacements: { id: requestId }, type: QueryTypes.SELECT });
  return fromRow(updated);
}

/** Chạy định kỳ: nhắc trước hạn và báo quá hạn nộp lại B/L gốc. Mỗi mốc chỉ báo một lần. */
async function processLoiDeadlines() {
  const notify = async (r, kind) => {
    const [file] = await sequelize.query('SELECT consignee_company_id FROM import_shipments WHERE id = :id', { replacements: { id: r.import_shipment_id }, type: QueryTypes.SELECT });
    const due = new Date(r.original_due_at).toLocaleDateString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' });
    const overdue = kind === 'OVERDUE';
    await recordDocumentEvent({
      direction: 'IMPORT', fileRef: r.bl_number, documentType: 'BL', action: overdue ? 'LOI_ORIGINAL_OVERDUE' : 'LOI_ORIGINAL_REMINDER', channel: 'SYSTEM',
      note: overdue ? `Quá hạn nộp lại B/L gốc (hạn ${due}) sau khi nhận hàng bằng LOI.` : `Sắp đến hạn nộp lại B/L gốc (hạn ${due}).`,
    });
    await notifyCompany(file?.consignee_company_id, r.bl_number, overdue ? 'LoiOriginalOverdue' : 'LoiOriginalReminder',
      overdue ? `Quá hạn nộp lại B/L gốc [${r.bl_number}]` : `Sắp đến hạn nộp lại B/L gốc [${r.bl_number}]`,
      overdue ? `Đã quá hạn ${due}: vui lòng nộp lại B/L gốc cho Hãng tàu theo cam kết trong thư bảo lãnh (LOI).` : `Hạn nộp lại B/L gốc theo thư bảo lãnh (LOI) là ${due}.`,
      `loi-${kind.toLowerCase()}-${r.id}`);
  };
  const [overdue] = await sequelize.query(
    `UPDATE bl_post_issuance_requests SET overdue_notified_at = now()
      WHERE request_type = 'LOI_RELEASE' AND status = 'APPROVED' AND original_returned_at IS NULL AND overdue_notified_at IS NULL AND original_due_at < now()
      RETURNING id, import_shipment_id, bl_number, original_due_at`);
  for (const r of overdue) await notify(r, 'OVERDUE');
  const [reminders] = await sequelize.query(
    `UPDATE bl_post_issuance_requests SET reminder_sent_at = now()
      WHERE request_type = 'LOI_RELEASE' AND status = 'APPROVED' AND original_returned_at IS NULL AND reminder_sent_at IS NULL AND overdue_notified_at IS NULL
        AND original_due_at >= now() AND original_due_at < now() + (:days * interval '1 day')
      RETURNING id, import_shipment_id, bl_number, original_due_at`, { replacements: { days: REMINDER_DAYS } });
  for (const r of reminders) await notify(r, 'REMINDER');
  return { reminded: reminders.length, overdue: overdue.length };
}

module.exports = { listForFile, create, cancel, resolve, changeSchedule, confirmOriginalReturned, processLoiDeadlines };

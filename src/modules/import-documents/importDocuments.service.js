'use strict';

/**
 * Chứng từ hàng nhập (đầu nhập): nhận pre-alert, đối chiếu B/L với manifest, gửi Arrival Notice,
 * xác nhận/yêu cầu sửa với Transport company, thu phí, kiểm quyền nhận hàng, phát hành D/O.
 *
 * Ở mỗi trạng thái chỉ một vai trò được chuyển hồ sơ sang bước tiếp theo; mọi thao tác ghi vào
 * shipment_document_events (direction = IMPORT, file_ref = số B/L).
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const shippingLineService = require('../shipping-line/shippingLine.service');
const repo = require('../shipping-line/shippingLine.repository');
const { recordDocumentEvent, getDocumentEvents } = require('../document-workflow/documentEvents');
const { notifyDoIssued } = require('../document-workflow/companyNotify');

const STATUS = Object.freeze({
  RECEIVED: 'RECEIVED',
  MISMATCH: 'MISMATCH',
  NOTICE_SENT: 'NOTICE_SENT',
  AMENDMENT_REQUESTED: 'AMENDMENT_REQUESTED',
  CONFIRMED: 'CONFIRMED',
  RELEASE_VERIFIED: 'RELEASE_VERIFIED',
  DO_ISSUED: 'DO_ISSUED',
});

// Thông tin hành chính của người nhận do đầu nhập cập nhật trực tiếp; thông tin trên B/L phải do đầu xuất sửa.
const ADMIN_FIELDS = ['invoice.companyName', 'invoice.taxCode', 'invoice.address', 'invoice.email', 'contact.name', 'contact.phone', 'contact.email'];
const BL_FIELDS = ['shipper', 'consignee', 'notifyParty', 'cargoDescription'];
const BL_COLUMNS = { shipper: 'shipper', consignee: 'consignee', notifyParty: 'notify_party', cargoDescription: 'cargo_description' };

const REQUIRED_DOCUMENTS = {
  Original: ['Xuất trình ít nhất một bản B/L gốc hợp lệ (có ký hậu đúng nếu B/L để "To order")'],
  TelexRelease: ['Chờ hãng tàu đối chiếu xác nhận surrender từ đầu xuất; không cần bản gốc'],
  SeawayBill: ['Giấy tờ chứng minh là consignee ghi trên vận đơn, hoặc giấy ủy quyền của consignee'],
};

function httpError(status, message) {
  const err = new Error(message);
  err.statusCode = status;
  return err;
}

const norm = (v) => String(v || '').replace(/\s+/g, ' ').trim().toLowerCase();
const normNo = (v) => String(v || '').replace(/\s+/g, '').toUpperCase();

function fromRow(r) {
  if (!r) return null;
  return {
    id: r.id,
    blNumber: r.bl_number,
    status: r.status,
    version: r.version,
    lineUserId: r.line_user_id,
    consigneeCompanyId: r.consignee_company_id,
    consigneeCompanyName: r.consignee_company_name,
    vesselName: r.vessel_name,
    voyageNumber: r.voyage_number,
    portOfLoading: r.port_of_loading,
    portOfDischarge: r.port_of_discharge,
    etd: r.etd,
    eta: r.eta,
    blType: r.bl_type,
    freightTerms: r.freight_terms,
    shipper: r.shipper,
    consignee: r.consignee,
    notifyParty: r.notify_party,
    cargoDescription: r.cargo_description,
    containers: r.containers || [],
    reconciledAt: r.reconciled_at,
    mismatch: r.mismatch,
    mismatchNote: r.mismatch_note,
    notice: r.notice,
    noticeVersion: r.notice_version,
    noticeDeadline: r.notice_deadline,
    autoAccepted: r.auto_accepted,
    invoiceInfo: r.invoice_info,
    contactInfo: r.contact_info,
    amendmentRequest: r.amendment_request,
    authorizationFileUrl: r.authorization_file_url,
    releaseCheck: r.release_check,
    doNumber: r.do_number,
    createdAt: r.created_at,
    updatedAt: r.updated_at,
  };
}

const json = (v) => (v === undefined || v === null ? null : JSON.stringify(v));

async function companyIdOf(user) {
  const [row] = await sequelize.query('SELECT carrier_id FROM carrier_users WHERE user_id = :id LIMIT 1', {
    replacements: { id: user.id }, type: QueryTypes.SELECT,
  });
  if (!row) throw httpError(403, 'Tài khoản chưa thuộc công ty vận tải nào.');
  return row.carrier_id;
}

const isLine = (user) => user.role === 'Shipping Line';
const needLine = (user) => { if (!isLine(user)) throw httpError(403, 'Thao tác này dành cho Hãng tàu.'); };
const needCompany = (user) => { if (!(user.role === 'Transport Company' || user.role === 'Carrier Staff')) throw httpError(403, 'Thao tác này dành cho Transport company của người nhận hàng.'); };
const isAdmin = (user) => user.role === 'Administrator';
const isCompany = (user) => user.role === 'Transport Company' || user.role === 'Carrier Staff';

async function loadFile(id, actor, { lock = false, transaction } = {}) {
  const [row] = await sequelize.query(
    `SELECT s.*, c.company_name AS consignee_company_name FROM import_shipments s
       LEFT JOIN carriers c ON c.id = s.consignee_company_id
      WHERE s.id = :id ${lock ? 'FOR UPDATE OF s' : ''}`,
    { replacements: { id }, type: QueryTypes.SELECT, transaction },
  );
  if (!row) throw httpError(404, 'Không tìm thấy hồ sơ hàng nhập.');
  // Chỉ tài khoản Transport company được gắn và Hãng tàu phụ trách mới xem và thao tác trên hồ sơ
  if (isAdmin(actor)) return row;
  if (isLine(actor) && (row.line_user_id === actor.id || await sameLine(row.line_user_id, actor.id))) return row;
  if (isCompany(actor) && row.consignee_company_id && row.consignee_company_id === (await companyIdOf(actor))) return row;
  throw httpError(404, 'Không tìm thấy hồ sơ hàng nhập.');
}

function requireStatus(row, ...allowed) {
  if (!allowed.includes(row.status)) {
    throw httpError(409, `Thao tác này không dùng được ở trạng thái hiện tại (${row.status}).`);
  }
}

async function notifyCompany(companyId, blNumber, eventType, title, message, dedupKey) {
  if (!companyId) return;
  try {
    await sequelize.query(
      `INSERT INTO cargo_notifications(recipient_company_id, company_tax_code, recipient_role, event_type, direction, priority, title, message, data,
                                       action_type, action_label, action_url, document_type, document_id, dedup_key)
       SELECT c.id, c.tax_code, 'Transport Company', :eventType, 'import', 'high', :title, :message, jsonb_build_object('blNumber', CAST(:bl AS text)),
              'view_details', 'Xem chi tiết', :url, 'ARRIVAL_NOTICE', CAST(:bl AS text), :dedup
         FROM carriers c WHERE c.id = :companyId AND NOT EXISTS (SELECT 1 FROM cargo_notifications n WHERE n.dedup_key = :dedup) LIMIT 1`,
      { replacements: { companyId, eventType, title, message, bl: blNumber, url: `/import-documents?bl=${encodeURIComponent(blNumber)}`, dedup: dedupKey } },
    );
  } catch (err) {
    console.warn('[ImportDocuments] Không tạo được thông báo:', err.message);
  }
}

// ── Nhận pre-alert → mở hồ sơ ─────────────────────────────────────────────────
async function listPreAlerts() {
  const [rows] = await sequelize.query(
    `SELECT p.id, p.bl_number AS "blNumber", p.booking_no AS "bookingNo", p.vessel_name AS "vesselName", p.voyage_number AS "voyageNumber",
            p.port_of_loading AS "portOfLoading", p.port_of_discharge AS "portOfDischarge", p.eta, p.bl_type AS "blType", p.freight_terms AS "freightTerms",
            p.shipper, p.consignee, p.status, p.sent_at AS "sentAt", p.received_at AS "receivedAt", jsonb_array_length(p.containers) AS "containerCount"
       FROM shipment_pre_alerts p ORDER BY p.sent_at DESC LIMIT 200`,
  );
  return rows;
}

async function receivePreAlert(preAlertId, actor) {
  needLine(actor);
  const created = await sequelize.transaction(async (transaction) => {
    const [alert] = await sequelize.query('SELECT * FROM shipment_pre_alerts WHERE id = :id FOR UPDATE', {
      replacements: { id: preAlertId }, type: QueryTypes.SELECT, transaction,
    });
    if (!alert) throw httpError(404, 'Không tìm thấy pre-alert.');
    if (alert.status === 'RECEIVED') throw httpError(409, 'Pre-alert này đã được tiếp nhận.');
    const p = alert.payload || {};
    const containers = (alert.containers || []).map((c) => ({
      containerNumber: c.containerNumber, sealNumber: c.sealNumber, isoType: c.isoType, grossWeightKg: c.vgmKg ?? c.grossWeightKg,
    }));

    // B/L chiều nhập trong bảng bills_of_lading, để kiểm tra điều kiện D/O dùng chung với phân hệ Hãng tàu
    const blType = alert.bl_type === 'SeawayBill' ? 'SEAWAY_BILL' : 'ORIGINAL';
    const blStatus = alert.bl_type === 'TelexRelease' ? 'TELEX_RELEASED' : 'ISSUED';
    await repo.insertBl({
      blNumber: alert.bl_number, direction: 'IMPORT', blType, voyageNumber: alert.voyage_number, vesselName: alert.vessel_name,
      pol: alert.port_of_loading, pod: alert.port_of_discharge, shipper: alert.shipper, consignee: alert.consignee,
      notifyParty: alert.notify_party, cargoDescription: p.cargoDescription || 'Theo pre-alert đầu xuất', packageCount: Number(p.packagesCount) || 1,
      grossWeightKg: containers.reduce((s, c) => s + (Number(c.grossWeightKg) || 0), 0),
      freightStatus: alert.freight_terms === 'Collect' ? 'COLLECT' : 'PREPAID', freightAmountUsd: 0,
      freightPaid: alert.freight_terms !== 'Collect', freightPaymentStatus: alert.freight_terms !== 'Collect' ? 'PAID' : 'UNPAID',
      localChargesStatus: 'UNPAID', status: blStatus, sourceType: 'PRE_ALERT', sourceReference: alert.booking_no,
      issueDate: new Date(), attachedFiles: [], containers,
    }, { transaction });

    const [row] = await sequelize.query(
      `INSERT INTO import_shipments(bl_number, pre_alert_id, line_user_id, vessel_name, voyage_number, port_of_loading, port_of_discharge, etd, eta,
                                    bl_type, freight_terms, shipper, consignee, notify_party, cargo_description, containers)
       VALUES (:bl, :alertId, :lineUserId, :vessel, :voyage, :pol, :pod, :etd, :eta, :blType, :terms, :shipper, :consignee, :notify, :cargo, CAST(:containers AS jsonb))
       RETURNING id`,
      {
        replacements: {
          bl: alert.bl_number, alertId: alert.id, lineUserId: actor.id, vessel: alert.vessel_name, voyage: alert.voyage_number,
          pol: alert.port_of_loading, pod: alert.port_of_discharge, etd: alert.etd, eta: alert.eta, blType: alert.bl_type, terms: alert.freight_terms,
          shipper: alert.shipper, consignee: alert.consignee, notify: alert.notify_party, cargo: p.cargoDescription || null, containers: json(containers),
        },
        type: QueryTypes.SELECT, transaction,
      },
    );
    await sequelize.query(`UPDATE shipment_pre_alerts SET status = 'RECEIVED', received_at = now() WHERE id = :id`, { replacements: { id: alert.id }, transaction });
    return { id: row.id, blNumber: alert.bl_number };
  });

  await recordDocumentEvent({
    direction: 'IMPORT', fileRef: created.blNumber, documentType: 'PRE_ALERT', action: 'RECEIVED', toStatus: STATUS.RECEIVED, actor,
    note: 'Đại lý đầu nhập nhận pre-alert (bản sao B/L, danh sách container, hình thức B/L, điều kiện cước).',
  });
  return get(created.id, actor);
}

// Các tài khoản cùng một hãng tàu (shipping_line_accounts) dùng chung hồ sơ của hãng.
async function sameLine(ownerUserId, actorId) {
  if (!ownerUserId) return false;
  const rows = await sequelize.query(
    'SELECT 1 FROM shipping_line_accounts a JOIN shipping_line_accounts me ON me.shipping_line_id = a.shipping_line_id WHERE me.user_id = :me AND a.user_id = :owner LIMIT 1',
    { replacements: { me: actorId, owner: ownerUserId }, type: QueryTypes.SELECT },
  );
  return rows.length > 0;
}
// ── Truy vấn ───────────────────────────────────────────────────────────────────
async function list(actor, { status, search } = {}) {
  const where = [];
  const replacements = {};
  if (isLine(actor)) { where.push("s.line_user_id IN (SELECT user_id FROM shipping_line_accounts WHERE shipping_line_id=(SELECT shipping_line_id FROM shipping_line_accounts WHERE user_id=:me) UNION SELECT :me::uuid)"); replacements.me = actor.id; }
  else if (isCompany(actor)) { where.push('s.consignee_company_id = :company'); replacements.company = await companyIdOf(actor); }
  if (status) { where.push('s.status = :status'); replacements.status = status; }
  if (search) { where.push('(s.bl_number ILIKE :q OR s.consignee ILIKE :q)'); replacements.q = `%${search}%`; }
  const rows = await sequelize.query(
    `SELECT s.*, c.company_name AS consignee_company_name FROM import_shipments s LEFT JOIN carriers c ON c.id = s.consignee_company_id
      ${where.length ? 'WHERE ' + where.join(' AND ') : ''} ORDER BY s.created_at DESC LIMIT 200`,
    { replacements, type: QueryTypes.SELECT },
  );
  return rows.map(fromRow);
}

async function get(id, actor) {
  const row = await loadFile(id, actor);
  const file = fromRow(row);
  file.events = await getDocumentEvents('IMPORT', file.blNumber);
  return file;
}

async function listCompanies() {
  // Chỉ công ty có tài khoản vai trò Transport Company đang hoạt động (người gửi / nhận hàng hợp lệ)
  const rows = await sequelize.query(
    `SELECT c.id, c.company_name AS "companyName", c.tax_code AS "taxCode" FROM carriers c
      WHERE EXISTS (SELECT 1 FROM carrier_users cu JOIN users u ON u.id = cu.user_id
                     WHERE cu.carrier_id = c.id AND u.role = 'Transport Company' AND u.is_active)
      ORDER BY c.company_name LIMIT 300`,
    { type: QueryTypes.SELECT },
  );
  return rows;
}

async function assignConsignee(id, carrierId, actor) {
  needLine(actor);
  if (!carrierId) throw httpError(400, 'Cần chọn công ty (Transport company) của người nhận hàng.');
  const [carrier] = await sequelize.query('SELECT id, company_name FROM carriers WHERE id = :id', { replacements: { id: carrierId }, type: QueryTypes.SELECT });
  if (!carrier) throw httpError(404, 'Không tìm thấy công ty.');
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.RECEIVED, STATUS.MISMATCH, STATUS.NOTICE_SENT);
  await sequelize.query('UPDATE import_shipments SET consignee_company_id = :c, version = version + 1, updated_at = now() WHERE id = :id', { replacements: { c: carrierId, id } });
  await recordDocumentEvent({
    direction: 'IMPORT', fileRef: row.bl_number, documentType: 'BL', action: 'CONSIGNEE_ASSIGNED', actor,
    fieldChanges: [{ field: 'consigneeCompany', old: row.consignee_company_id, new: carrierId }], note: carrier.company_name,
  });
  return get(id, actor);
}

// ── Đối chiếu B/L với manifest ────────────────────────────────────────────────
async function findManifestBill(blNumber) {
  const rows = await sequelize.query(
    `SELECT m.payload FROM shipping_manifests m JOIN shipping_manifest_bills b ON b.manifest_id = m.id
      WHERE upper(btrim(b.bl_number)) = :bl ORDER BY m.created_at DESC LIMIT 1`,
    { replacements: { bl: normNo(blNumber) }, type: QueryTypes.SELECT },
  );
  const bills = rows[0]?.payload?.bills || [];
  return bills.find((b) => normNo(b.blNumber) === normNo(blNumber)) || null;
}

/** So từng trường của B/L (theo pre-alert) với dòng tương ứng trên manifest. */
function compareBillWithManifest(file, manifest) {
  const diffs = [];
  const add = (field, blValue, manifestValue) => diffs.push({ field, blValue: blValue ?? null, manifestValue: manifestValue ?? null });
  if (norm(file.shipper) !== norm(manifest.shipper)) add('shipper', file.shipper, manifest.shipper);
  if (norm(file.consignee) !== norm(manifest.consignee)) add('consignee', file.consignee, manifest.consignee);
  const mine = new Map((file.containers || []).map((c) => [normNo(c.containerNumber), c]));
  const theirs = new Map((manifest.containers || []).map((c) => [normNo(c.containerNumber), c]));
  for (const [no, c] of mine) {
    const m = theirs.get(no);
    if (!m) { add(`container:${no}`, 'có trên B/L', 'không có trên manifest'); continue; }
    if (normNo(c.sealNumber) !== normNo(m.sealNumber)) add(`container:${no}.sealNumber`, c.sealNumber, m.sealNumber);
    if (c.isoType && m.isoType && normNo(c.isoType) !== normNo(m.isoType)) add(`container:${no}.isoType`, c.isoType, m.isoType);
    if (Number(c.grossWeightKg) !== Number(m.grossWeightKg)) add(`container:${no}.grossWeightKg`, c.grossWeightKg, m.grossWeightKg);
  }
  for (const no of theirs.keys()) if (!mine.has(no)) add(`container:${no}`, 'không có trên B/L', 'có trên manifest');
  return diffs;
}

async function reconcile(id, { manifestBill } = {}, actor) {
  needLine(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.RECEIVED);
  const manifest = manifestBill || (await findManifestBill(row.bl_number));
  if (!manifest) throw httpError(422, 'Chưa có manifest chứa B/L này. Nhập dòng manifest để đối chiếu hoặc chờ manifest được gửi.');
  const diffs = compareBillWithManifest(fromRow(row), manifest);
  if (diffs.length === 0) {
    await sequelize.query(`UPDATE import_shipments SET reconciled_at = now(), mismatch = NULL, mismatch_note = NULL, version = version + 1, updated_at = now() WHERE id = :id`, { replacements: { id } });
    await recordDocumentEvent({ direction: 'IMPORT', fileRef: row.bl_number, documentType: 'BL', action: 'RECONCILED', fromStatus: row.status, toStatus: STATUS.RECEIVED, actor, note: 'B/L khớp manifest.' });
  } else {
    await sequelize.query(`UPDATE import_shipments SET status = 'MISMATCH', mismatch = CAST(:m AS jsonb), reconciled_at = NULL, version = version + 1, updated_at = now() WHERE id = :id`, { replacements: { id, m: json(diffs) } });
    await recordDocumentEvent({
      direction: 'IMPORT', fileRef: row.bl_number, documentType: 'BL', action: 'MISMATCH_FOUND', fromStatus: row.status, toStatus: STATUS.MISMATCH, actor,
      fieldChanges: diffs.map((d) => ({ field: d.field, old: d.blValue, new: d.manifestValue })),
      note: 'B/L lệch manifest. Không tự sửa; chuyển yêu cầu kiểm tra cho đầu xuất.',
    });
  }
  return get(id, actor);
}

/**
 * Đầu xuất trả lời khi B/L lệch manifest. CORRECTED: cập nhật theo bản sửa chính thức rồi quay lại bước đối chiếu.
 * CONFIRMED_AS_IS: đầu xuất xác nhận dữ liệu B/L đúng, hồ sơ coi như đã đối chiếu.
 */
async function resolveMismatch(id, { answer, correctedFields, note } = {}, actor) {
  needLine(actor);
  if (!note || !String(note).trim()) throw httpError(400, 'Cần ghi lại nội dung trả lời của đầu xuất.');
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.MISMATCH);
  if (answer === 'CONFIRMED_AS_IS') {
    await sequelize.query(`UPDATE import_shipments SET status = 'RECEIVED', reconciled_at = now(), mismatch_note = :note, version = version + 1, updated_at = now() WHERE id = :id`, { replacements: { id, note } });
  } else if (answer === 'CORRECTED') {
    const patch = {};
    for (const key of ['shipper', 'consignee', 'notifyParty', 'cargoDescription']) {
      if (correctedFields && correctedFields[key] != null) patch[key] = String(correctedFields[key]).trim();
    }
    const sets = Object.keys(patch).map((k) => `${BL_COLUMNS[k]} = :${k}`);
    const replacements = { id, note, ...patch };
    if (Array.isArray(correctedFields?.containers)) {
      sets.push('containers = CAST(:containers AS jsonb)');
      replacements.containers = json(correctedFields.containers);
      patch.containers = correctedFields.containers;
    }
    if (sets.length === 0) throw httpError(400, 'Cần nêu các trường đã được đầu xuất sửa.');
    await sequelize.query(
      `UPDATE import_shipments SET ${sets.join(', ')}, status = 'RECEIVED', reconciled_at = NULL, mismatch = NULL, mismatch_note = :note, version = version + 1, updated_at = now() WHERE id = :id`,
      { replacements },
    );
    const blPatch = {};
    for (const key of ['shipper', 'consignee', 'notifyParty', 'cargoDescription', 'containers']) if (key in patch) blPatch[key] = patch[key];
    const bl = await repo.findBl(row.bl_number);
    if (bl) await repo.updateBl(bl.id, blPatch);
  } else {
    throw httpError(400, 'answer phải là CORRECTED hoặc CONFIRMED_AS_IS.');
  }
  await recordDocumentEvent({
    direction: 'IMPORT', fileRef: row.bl_number, documentType: 'BL', action: 'ORIGIN_REPLIED', fromStatus: STATUS.MISMATCH, toStatus: STATUS.RECEIVED, actor, note,
    fieldChanges: answer === 'CORRECTED' ? Object.entries(correctedFields || {}).filter(([k]) => k !== 'containers').map(([field, value]) => ({ field, old: row[BL_COLUMNS[field]] ?? null, new: value })) : [],
  });
  return get(id, actor);
}

// ── Arrival Notice ─────────────────────────────────────────────────────────────
function noticeDeadline(eta, now = new Date()) {
  const min = new Date(now.getTime() + 2 * 3600e3);
  const max = new Date(now.getTime() + 72 * 3600e3);
  const beforeEta = eta ? new Date(new Date(eta).getTime() - 12 * 3600e3) : max;
  const d = beforeEta < max ? beforeEta : max;
  return d < min ? min : d;
}

function buildNotice(file, version, options, deadline) {
  const fees = (options.fees || []).map((f) => ({ name: String(f.name || '').trim(), amountVnd: Number(f.amountVnd) || 0 })).filter((f) => f.name);
  return {
    version,
    sentAt: new Date().toISOString(),
    bl: {
      blNumber: file.bl_number, shipper: file.shipper, consignee: file.consignee, notifyParty: file.notify_party, cargoDescription: file.cargo_description,
      containers: file.containers, blType: file.bl_type, freightTerms: file.freight_terms,
    },
    vesselName: file.vessel_name, voyageNumber: file.voyage_number, eta: file.eta, portOfDischarge: file.port_of_discharge,
    stockLocation: options.stockLocation || 'Bãi container Cảng Tiên Sa',
    fees, totalFeesVnd: fees.reduce((s, f) => s + f.amountVnd, 0),
    freeTime: {
      demDays: Number(options.freeTime?.demDays) || 5, detDays: Number(options.freeTime?.detDays) || 7,
      startsFrom: options.freeTime?.startsFrom || 'Ngày dỡ container khỏi tàu',
      overdueFeeNote: options.freeTime?.overdueFeeNote || 'Phí lưu bãi/lưu container quá hạn theo biểu phí hiện hành',
    },
    requiredDocuments: options.requiredDocuments?.length ? options.requiredDocuments : (REQUIRED_DOCUMENTS[file.bl_type] || REQUIRED_DOCUMENTS.Original),
    deadline: deadline.toISOString(),
  };
}

async function sendArrivalNotice(id, options = {}, actor) {
  needLine(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.RECEIVED);
  if (!row.reconciled_at) throw httpError(409, 'Cần đối chiếu B/L với manifest và không còn lệch trước khi gửi Arrival Notice.');
  if (!row.consignee_company_id) throw httpError(409, 'Cần gắn hồ sơ với Transport company của người nhận hàng trước khi gửi Arrival Notice.');
  const deadline = options.deadline ? new Date(options.deadline) : noticeDeadline(row.eta);
  if (Number.isNaN(deadline.getTime()) || deadline.getTime() <= Date.now()) throw httpError(400, 'Hạn chót phản hồi phải ở tương lai.');
  const notice = buildNotice(row, 1, options, deadline);
  await sequelize.query(
    `UPDATE import_shipments SET status = 'NOTICE_SENT', notice = CAST(:n AS jsonb), notice_version = 1, notice_deadline = :d, version = version + 1, updated_at = now() WHERE id = :id`,
    { replacements: { id, n: json(notice), d: deadline } },
  );
  await recordDocumentEvent({ direction: 'IMPORT', fileRef: row.bl_number, documentType: 'ARRIVAL_NOTICE', action: 'SENT', fromStatus: STATUS.RECEIVED, toStatus: STATUS.NOTICE_SENT, actor, documentVersion: 1, snapshot: notice });
  await notifyCompany(row.consignee_company_id, row.bl_number, 'ArrivalNoticeIssued', `Arrival Notice [${row.bl_number}] cần xác nhận`,
    `Hãng tàu đã gửi Arrival Notice cho vận đơn ${row.bl_number}. Hạn xác nhận: ${deadline.toLocaleString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' })}. Vui lòng kiểm tra thông tin, các khoản phí và free time.`, `an-${row.bl_number}-v1`);
  return get(id, actor);
}

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

async function confirmNotice(id, payload = {}, actor) {
  needCompany(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.NOTICE_SENT);
  if (Number(payload.noticeVersion) !== row.notice_version) throw httpError(409, 'Arrival Notice đã được cập nhật. Tải lại và kiểm tra phiên bản mới trước khi xác nhận.');
  const invoice = payload.invoiceInfo || {};
  const contact = payload.contactInfo || {};
  for (const [label, value] of [['Tên công ty xuất hóa đơn', invoice.companyName], ['Mã số thuế', invoice.taxCode], ['Địa chỉ', invoice.address], ['Người liên hệ nhận D/O', contact.name], ['Số điện thoại liên hệ', contact.phone]]) {
    if (!value || !String(value).trim()) throw httpError(400, `${label} là bắt buộc.`);
  }
  if (!EMAIL.test(String(invoice.email || ''))) throw httpError(400, 'Email nhận hóa đơn không hợp lệ.');
  if (payload.freeTimeAcknowledged !== true) throw httpError(400, 'Cần xác nhận đã biết các khoản phí, số ngày free time và ngày dự kiến nhận hàng.');
  await sequelize.query(
    `UPDATE import_shipments SET status = 'CONFIRMED', invoice_info = CAST(:i AS jsonb), contact_info = CAST(:c AS jsonb), version = version + 1, updated_at = now() WHERE id = :id`,
    { replacements: { id, i: json(invoice), c: json({ ...contact, expectedPickupDate: payload.expectedPickupDate || null }) } },
  );
  await recordDocumentEvent({ direction: 'IMPORT', fileRef: row.bl_number, documentType: 'ARRIVAL_NOTICE', action: 'CONFIRMED', fromStatus: STATUS.NOTICE_SENT, toStatus: STATUS.CONFIRMED, actor, documentVersion: row.notice_version });
  return get(id, actor);
}

function classifyChanges(changes) {
  const fields = changes.map((c) => c.field);
  const admin = fields.filter((f) => ADMIN_FIELDS.includes(f));
  const bl = fields.filter((f) => BL_FIELDS.includes(f));
  const unknown = fields.filter((f) => !ADMIN_FIELDS.includes(f) && !BL_FIELDS.includes(f));
  if (unknown.length) throw httpError(400, `Trường không được phép sửa: ${unknown.join(', ')}`);
  if (admin.length && bl.length) throw httpError(400, 'Hãy tách riêng yêu cầu sửa thông tin hành chính và yêu cầu sửa thông tin trên B/L.');
  return bl.length ? 'BL' : 'ADMIN';
}

const currentValue = (row, field) => {
  if (BL_FIELDS.includes(field)) return row[BL_COLUMNS[field]];
  const [group, key] = field.split('.');
  return (group === 'invoice' ? row.invoice_info : row.contact_info)?.[key] ?? null;
};

async function requestAmendment(id, payload = {}, actor) {
  needCompany(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.NOTICE_SENT);
  if (Number(payload.noticeVersion) !== row.notice_version) throw httpError(409, 'Arrival Notice đã được cập nhật. Tải lại trước khi gửi yêu cầu sửa.');
  if (!payload.reason || !String(payload.reason).trim()) throw httpError(400, 'Cần nêu lý do sửa.');
  const changes = (payload.changes || []).map((c) => ({ field: c.field, newValue: String(c.newValue ?? '').trim() }));
  if (changes.length === 0 || changes.some((c) => !c.newValue)) throw httpError(400, 'Cần nêu trường sai và nội dung đúng.');
  const type = classifyChanges(changes);
  const request = {
    type, reason: String(payload.reason).trim(), requestedAt: new Date().toISOString(), requestedBy: actor.id,
    noticeVersion: row.notice_version, changes: changes.map((c) => ({ ...c, oldValue: currentValue(row, c.field) })),
    forwardedToOriginAt: type === 'BL' ? new Date().toISOString() : null,
  };
  await sequelize.query(`UPDATE import_shipments SET status = 'AMENDMENT_REQUESTED', amendment_request = CAST(:r AS jsonb), version = version + 1, updated_at = now() WHERE id = :id`, { replacements: { id, r: json(request) } });
  await recordDocumentEvent({
    direction: 'IMPORT', fileRef: row.bl_number, documentType: 'ARRIVAL_NOTICE', action: 'REVISION_REQUESTED', fromStatus: STATUS.NOTICE_SENT, toStatus: STATUS.AMENDMENT_REQUESTED, actor,
    documentVersion: row.notice_version, fieldChanges: request.changes.map((c) => ({ field: c.field, old: c.oldValue, new: c.newValue })), note: request.reason,
  });
  return get(id, actor);
}

/**
 * Hãng tàu xử lý yêu cầu sửa. Thông tin hành chính: đầu nhập cập nhật trực tiếp.
 * Thông tin trên B/L: chuyển đầu xuất, chỉ thực hiện khi Transport company phía shipper đã xác nhận.
 */
async function resolveAmendment(id, { accept, note, shipperConfirmed } = {}, actor) {
  needLine(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.AMENDMENT_REQUESTED);
  const request = row.amendment_request;
  if (!accept) {
    if (!note || !String(note).trim()) throw httpError(400, 'Cần nêu lý do từ chối.');
    await sequelize.query(`UPDATE import_shipments SET status = 'NOTICE_SENT', amendment_request = NULL, version = version + 1, updated_at = now() WHERE id = :id`, { replacements: { id } });
    await recordDocumentEvent({ direction: 'IMPORT', fileRef: row.bl_number, documentType: 'ARRIVAL_NOTICE', action: 'AMENDMENT_REJECTED', fromStatus: STATUS.AMENDMENT_REQUESTED, toStatus: STATUS.NOTICE_SENT, actor, note });
    await notifyCompany(row.consignee_company_id, row.bl_number, 'ArrivalNoticeAmendmentRejected', `Yêu cầu sửa Arrival Notice [${row.bl_number}] bị từ chối`, String(note), `an-rej-${row.bl_number}-${row.version}`);
    return get(id, actor);
  }
  if (request.type === 'BL' && shipperConfirmed !== true) {
    throw httpError(409, 'Thông tin trên B/L chỉ được sửa khi đầu xuất chuyển yêu cầu và Transport company phía shipper xác nhận. Tích xác nhận của shipper sau khi đã có văn bản.');
  }
  const sets = [];
  const replacements = { id };
  let invoice = { ...(row.invoice_info || {}) };
  let contact = { ...(row.contact_info || {}) };
  const blPatch = {};
  for (const change of request.changes) {
    if (BL_FIELDS.includes(change.field)) {
      sets.push(`${BL_COLUMNS[change.field]} = :${change.field}`);
      replacements[change.field] = change.newValue;
      blPatch[change.field] = change.newValue;
    } else {
      const [group, key] = change.field.split('.');
      if (group === 'invoice') invoice[key] = change.newValue; else contact[key] = change.newValue;
    }
  }
  const updated = { ...row };
  for (const [k, v] of Object.entries(blPatch)) updated[BL_COLUMNS[k]] = v;
  const nextVersion = row.notice_version + 1;
  const deadline = noticeDeadline(row.eta);
  const notice = buildNotice(updated, nextVersion, {
    fees: row.notice?.fees, stockLocation: row.notice?.stockLocation, freeTime: row.notice?.freeTime, requiredDocuments: row.notice?.requiredDocuments,
  }, deadline);
  sets.push('invoice_info = CAST(:invoice AS jsonb)', 'contact_info = CAST(:contact AS jsonb)', 'notice = CAST(:notice AS jsonb)');
  Object.assign(replacements, { invoice: json(invoice), contact: json(contact), notice: json(notice), d: deadline, nv: nextVersion });
  await sequelize.query(
    `UPDATE import_shipments SET ${sets.join(', ')}, status = 'NOTICE_SENT', amendment_request = NULL, notice_version = :nv, notice_deadline = :d, version = version + 1, updated_at = now() WHERE id = :id`,
    { replacements },
  );
  if (Object.keys(blPatch).length) {
    const bl = await repo.findBl(row.bl_number);
    if (bl) await repo.updateBl(bl.id, blPatch);
  }
  await recordDocumentEvent({
    direction: 'IMPORT', fileRef: row.bl_number, documentType: 'ARRIVAL_NOTICE', action: 'AMENDMENT_ACCEPTED', fromStatus: STATUS.AMENDMENT_REQUESTED, toStatus: STATUS.NOTICE_SENT, actor,
    documentVersion: nextVersion, snapshot: notice, note: request.type === 'BL' ? 'Sửa thông tin trên B/L theo xác nhận của shipper qua đầu xuất.' : 'Cập nhật thông tin hành chính của người nhận.',
    fieldChanges: request.changes.map((c) => ({ field: c.field, old: c.oldValue, new: c.newValue })),
  });
  await notifyCompany(row.consignee_company_id, row.bl_number, 'ArrivalNoticeIssued', `Arrival Notice [${row.bl_number}] đã được cập nhật`,
    'Hãng tàu đã chấp nhận yêu cầu sửa và gửi lại Arrival Notice. Vui lòng kiểm tra và xác nhận trước hạn chót.', `an-${row.bl_number}-v${nextVersion}`);
  return get(id, actor);
}

/** Quá hạn mà người nhận không phản hồi: thông tin giữ nguyên, người nhận chịu phí phát sinh do chậm nhận hàng. */
async function autoAcceptOverdue() {
  const [rows] = await sequelize.query(
    `UPDATE import_shipments SET status = 'CONFIRMED', auto_accepted = true, version = version + 1, updated_at = now()
      WHERE status = 'NOTICE_SENT' AND notice_deadline IS NOT NULL AND notice_deadline < now()
      RETURNING id, bl_number, consignee_company_id, notice_version`,
  );
  for (const r of rows) {
    await recordDocumentEvent({
      direction: 'IMPORT', fileRef: r.bl_number, documentType: 'ARRIVAL_NOTICE', action: 'AUTO_ACCEPTED', fromStatus: STATUS.NOTICE_SENT, toStatus: STATUS.CONFIRMED, channel: 'SYSTEM',
      documentVersion: r.notice_version, note: 'Quá hạn xác nhận mà người nhận không phản hồi: giữ nguyên thông tin Arrival Notice; người nhận chịu trách nhiệm về phí phát sinh do chậm nhận hàng.',
    });
    await notifyCompany(r.consignee_company_id, r.bl_number, 'ArrivalNoticeAutoAccepted', `Arrival Notice [${r.bl_number}] được giữ nguyên do quá hạn`,
      'Đã quá hạn xác nhận Arrival Notice. Thông tin được giữ nguyên và bạn chịu trách nhiệm về phí phát sinh do chậm nhận hàng.', `an-auto-${r.bl_number}-v${r.notice_version}`);
  }
  return rows.length;
}

// ── Thu phí, quyền nhận hàng, D/O ─────────────────────────────────────────────
async function collectFees(id, actor) {
  needLine(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.CONFIRMED, STATUS.RELEASE_VERIFIED);
  const result = await shippingLineService.collectBlFees(row.bl_number, actor.username);
  return { ...(await get(id, actor)), eligibility: result.eligibility };
}

async function confirmOriginalReceived(id, actor) {
  needLine(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.CONFIRMED, STATUS.RELEASE_VERIFIED);
  if (row.bl_type !== 'Original') throw httpError(409, 'Chỉ B/L gốc mới cần thu hồi bản gốc.');
  const result = await shippingLineService.surrenderBl(row.bl_number, actor.username);
  return { ...(await get(id, actor)), eligibility: result.eligibility };
}

async function setAuthorization(id, url, actor) {
  needCompany(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.CONFIRMED, STATUS.RELEASE_VERIFIED, STATUS.NOTICE_SENT, STATUS.AMENDMENT_REQUESTED);
  if (!url || !/^(https?:\/\/|\/uploads\/)/i.test(String(url))) throw httpError(400, 'Cần tải lên giấy ủy quyền của consignee.');
  await sequelize.query('UPDATE import_shipments SET authorization_file_url = :u, version = version + 1, updated_at = now() WHERE id = :id', { replacements: { id, u: url } });
  await recordDocumentEvent({ direction: 'IMPORT', fileRef: row.bl_number, documentType: 'DO', action: 'AUTHORIZATION_UPLOADED', actor, note: 'Transport company tải lên giấy ủy quyền của consignee.' });
  return get(id, actor);
}

async function requestDo(id, actor) {
  needCompany(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.CONFIRMED);
  const check = { ...(row.release_check || {}), requestedAt: new Date().toISOString(), requestedBy: actor.id };
  await sequelize.query('UPDATE import_shipments SET release_check = CAST(:c AS jsonb), version = version + 1, updated_at = now() WHERE id = :id', { replacements: { id, c: json(check) } });
  await recordDocumentEvent({ direction: 'IMPORT', fileRef: row.bl_number, documentType: 'DO', action: 'REQUESTED', fromStatus: row.status, toStatus: row.status, actor });
  return get(id, actor);
}

/** Người nhận có phải consignee ghi trên vận đơn không? Nếu không, bắt buộc có giấy ủy quyền. */
function actingForConsignee(row) {
  if (!row.consignee_company_name) return false;
  const a = norm(row.consignee_company_name);
  const b = norm(row.consignee);
  return Boolean(a && b && (a === b || a.includes(b) || b.includes(a)));
}

async function verifyRelease(id, actor) {
  needLine(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.CONFIRMED, STATUS.RELEASE_VERIFIED);
  const eligibility = await shippingLineService.checkEdoEligibility(row.bl_number);
  const reasons = [...(eligibility.reasons || [])];
  const isConsignee = actingForConsignee(row);
  if (!isConsignee && !row.authorization_file_url) {
    reasons.push('Transport company không phải consignee ghi trên vận đơn: cần tải lên giấy ủy quyền của consignee.');
  }
  const ok = Boolean(eligibility.eligible) && (isConsignee || Boolean(row.authorization_file_url));
  const check = { ...(row.release_check || {}), checkedAt: new Date().toISOString(), eligible: ok, isConsignee, reasons, criteria: eligibility.criteria };
  await sequelize.query(
    `UPDATE import_shipments SET release_check = CAST(:c AS jsonb), status = :s, version = version + 1, updated_at = now() WHERE id = :id`,
    { replacements: { id, c: json(check), s: ok ? STATUS.RELEASE_VERIFIED : STATUS.CONFIRMED } },
  );
  await recordDocumentEvent({
    direction: 'IMPORT', fileRef: row.bl_number, documentType: 'DO', action: ok ? 'RELEASE_VERIFIED' : 'RELEASE_BLOCKED', fromStatus: row.status, toStatus: ok ? STATUS.RELEASE_VERIFIED : STATUS.CONFIRMED, actor,
    note: ok ? 'Đã thu đủ phí và xác nhận quyền nhận hàng.' : reasons.join(' | '),
  });
  return { ...(await get(id, actor)), eligibility };
}

async function issueDo(id, options = {}, actor) {
  needLine(actor);
  const row = await loadFile(id, actor);
  requireStatus(row, STATUS.RELEASE_VERIFIED);
  const edo = await shippingLineService.issueEdo({
    blNumber: row.bl_number, validUntil: options.validUntil, freeTimeDays: options.freeTimeDays || row.notice?.freeTime?.demDays, notes: options.notes,
  }, actor.username);
  await sequelize.query(`UPDATE import_shipments SET status = 'DO_ISSUED', do_number = :n, version = version + 1, updated_at = now() WHERE id = :id`, { replacements: { id, n: edo.edoNumber } });
  await notifyDoIssued({ blNumber: row.bl_number, companyId: row.consignee_company_id, edo, containers: row.containers, stockLocation: row.notice?.stockLocation });
  if (row.bl_type === 'Original') {
    await recordDocumentEvent({ direction: 'IMPORT', fileRef: row.bl_number, documentType: 'BL', action: 'ACCOMPLISHED', actor, note: 'Bản B/L gốc đã thu hồi được đánh dấu hết hiệu lực, không thể dùng lại.' });
  }
  return { ...(await get(id, actor)), edo };
}

module.exports = {
  STATUS, listPreAlerts, receivePreAlert, list, get, listCompanies, assignConsignee, reconcile, resolveMismatch, sendArrivalNotice,
  confirmNotice, requestAmendment, resolveAmendment, autoAcceptOverdue, collectFees, confirmOriginalReceived, setAuthorization,
  requestDo, verifyRelease, issueDo, compareBillWithManifest, noticeDeadline,
  // Dùng chung cho yêu cầu ngoại lệ sau phát hành B/L
  loadFile, httpError, needLine, needCompany, notifyCompany, buildNotice, isLine,
};

'use strict';

/**
 * Lưu trữ B/L và e-D/O của phân hệ Hãng tàu trong PostgreSQL (bảng bills_of_lading, delivery_orders).
 * Service làm việc với dạng camelCase như cũ; file này chuyển đổi qua lại với dạng cột DB.
 */

const { randomUUID } = require('crypto');
const { sequelize } = require('../../config/database');

const TZ = 'Asia/Ho_Chi_Minh';

// ── Ngày giờ: DB là timestamptz, API giữ dạng 'YYYY-MM-DD HH:mm' (giờ Việt Nam) như bản cũ ──
function fromDb(d) {
  if (!d) return null;
  const date = d instanceof Date ? d : new Date(d);
  if (Number.isNaN(date.getTime())) return null;
  return date.toLocaleString('sv-SE', { timeZone: TZ }).slice(0, 16);
}

function toDb(s) {
  if (!s) return null;
  if (s instanceof Date) return s.toISOString();
  const str = String(s).trim();
  if (/[zZ]$|[+-]\d{2}:?\d{2}$/.test(str)) return str;
  return `${str.replace(' ', 'T')}${str.length <= 10 ? 'T00:00:00' : ''}+07:00`;
}

const norm = (v) => String(v || '').replace(/\s+/g, '').toUpperCase();

// ── Container trong JSONB: dữ liệu cũ dùng containerNo/sealNo/type, API dùng containerNumber/sealNumber/isoType ──
function normalizeContainer(c) {
  if (!c || typeof c !== 'object') return c;
  return {
    ...c,
    containerNumber: c.containerNumber || c.containerNo,
    sealNumber: c.sealNumber ?? c.sealNo,
    isoType: c.isoType || c.type,
    grossWeightKg: c.grossWeightKg ?? c.grossWeight,
  };
}

// ── B/L ───────────────────────────────────────────────────────────────────────
function blFromRow(r) {
  if (!r) return null;
  return {
    id: r.id,
    blNumber: r.bl_number,
    direction: String(r.direction || '').toUpperCase() || 'IMPORT',
    blType: r.bl_type || 'ORIGINAL',
    voyageNumber: r.voyage_number,
    vesselName: r.vessel_name,
    pol: r.pol || r.port_of_loading,
    pod: r.pod || r.port_of_discharge,
    shipper: r.shipper_name,
    consignee: r.consignee_name,
    notifyParty: r.notify_party,
    cargoDescription: r.cargo_description,
    packageCount: r.package_count,
    packageUnit: r.package_unit,
    grossWeightKg: r.total_gross_weight === null ? null : Number(r.total_gross_weight),
    measurementCbm: r.measurement_cbm === null ? null : Number(r.measurement_cbm),
    freightStatus: String(r.freight_status || '').toUpperCase().includes('COLLECT') ? 'COLLECT' : 'PREPAID',
    freightAmountUsd: r.freight_amount_usd === null ? 0 : Number(r.freight_amount_usd),
    freightPaid: Boolean(r.freight_paid),
    freightPaymentStatus: r.freight_payment_status || (r.freight_paid ? 'PAID' : 'UNPAID'),
    localChargesStatus: r.local_charges_status || (r.local_charges_paid ? 'PAID' : 'UNPAID'),
    status: r.status,
    sourceType: r.source_type || 'MANUAL',
    sourceReference: r.source_reference,
    issueDate: fromDb(r.issue_date || r.created_at),
    attachedFiles: r.attached_files || [],
    containers: Array.isArray(r.containers) ? r.containers.map(normalizeContainer) : [],
    consigneeHold: Boolean(r.consignee_hold),
    consigneeHoldReason: r.consignee_hold_reason,
    isCustomsHold: Boolean(r.is_customs_hold),
    isCarrierHold: Boolean(r.is_carrier_hold || r.liner_hold),
    inManifest: r.in_manifest !== false,
    holdReason: r.liner_hold_reason,
    bookingNo: r.booking_no,
    shipperTaxCode: r.shipper_tax_code,
    consigneeTaxCode: r.consignee_tax_code,
    draftVersion: r.draft_version,
    // Cờ suy ra khi ghép với e-D/O (service điền)
    edoIssued: false,
    edoNumber: null,
  };
}

function blToRow(b) {
  return {
    id: b.id,
    bl_number: b.blNumber,
    direction: String(b.direction || 'IMPORT').toLowerCase(),
    bl_type: b.blType || 'ORIGINAL',
    voyage_number: b.voyageNumber,
    vessel_name: b.vesselName,
    pol: b.pol,
    pod: b.pod,
    shipper_name: b.shipper,
    consignee_name: b.consignee,
    notify_party: b.notifyParty,
    cargo_description: b.cargoDescription,
    package_count: b.packageCount,
    package_unit: b.packageUnit,
    total_gross_weight: b.grossWeightKg,
    measurement_cbm: b.measurementCbm,
    freight_status: b.freightStatus,
    freight_amount_usd: b.freightAmountUsd,
    freight_paid: Boolean(b.freightPaid),
    freight_payment_status: b.freightPaymentStatus,
    local_charges_status: b.localChargesStatus || 'UNPAID',
    local_charges_paid: b.localChargesStatus === 'PAID',
    status: b.status,
    source_type: b.sourceType,
    source_reference: b.sourceReference,
    booking_no: b.bookingNo,
    issue_date: toDb(b.issueDate),
    attached_files: JSON.stringify(b.attachedFiles || []),
    containers: JSON.stringify(b.containers || []),
    consignee_hold: Boolean(b.consigneeHold),
    consignee_hold_reason: b.consigneeHoldReason || null,
    is_customs_hold: Boolean(b.isCustomsHold),
    is_carrier_hold: Boolean(b.isCarrierHold),
    liner_hold: Boolean(b.isCarrierHold),
    liner_hold_reason: b.holdReason || null,
    in_manifest: b.inManifest !== false,
  };
}

const BL_JSON_COLS = new Set(['attached_files', 'containers']);

// ── e-D/O ─────────────────────────────────────────────────────────────────────
// DB dùng RELEASED/HELD/CANCELLED (cả các module cổng, bãi cũng đọc cột này); API giữ ISSUED/EXPIRED/PENDING/CANCELLED.
function doStatusFromDb(status, validUntil) {
  if (status === 'RELEASED' || status === 'ISSUED') {
    return validUntil && new Date(validUntil) < new Date() ? 'EXPIRED' : 'ISSUED';
  }
  return status;
}

function doStatusToDb(status) {
  return status === 'ISSUED' || status === 'EXPIRED' ? 'RELEASED' : status;
}

function doFromRow(r) {
  if (!r) return null;
  return {
    id: r.id,
    edoNumber: r.do_number,
    blNumber: r.bl_number,
    voyageNumber: r.voyage_no,
    vesselName: r.vessel_name,
    consignee: r.consignee_name,
    consigneeTaxCode: r.consignee_tax_code,
    consigneeCompanyId: r.consignee_company_id,
    issueDate: fromDb(r.issued_date || r.created_at),
    validUntil: fromDb(r.valid_until),
    emptyReturnDepot: r.empty_return_depot,
    freeTimeDays: r.free_time_days,
    pinCode: r.pin_code,
    status: doStatusFromDb(r.status, r.valid_until),
    issuedBy: r.issued_by,
    releaseOfficer: r.release_officer,
    containers: Array.isArray(r.containers) ? r.containers.map(normalizeContainer) : [],
    notes: r.notes || r.hold_reason,
    cancelledAt: fromDb(r.cancelled_at),
    cancelReason: r.cancel_reason,
    cancelledBy: r.cancelled_by,
  };
}

function doToRow(d) {
  return {
    id: d.id,
    do_number: d.edoNumber,
    bl_number: d.blNumber,
    voyage_no: d.voyageNumber,
    vessel_name: d.vesselName,
    consignee_name: d.consignee,
    consignee_tax_code: d.consigneeTaxCode || null,
    consignee_company_id: d.consigneeCompanyId || null,
    issued_date: toDb(d.issueDate),
    valid_until: toDb(d.validUntil),
    empty_return_depot: d.emptyReturnDepot,
    free_time_days: d.freeTimeDays,
    pin_code: d.pinCode,
    status: doStatusToDb(d.status),
    issued_by: d.issuedBy,
    release_officer: d.releaseOfficer,
    containers: JSON.stringify(d.containers || []),
    notes: d.notes,
    cancelled_at: toDb(d.cancelledAt),
    cancel_reason: d.cancelReason,
    cancelled_by: d.cancelledBy,
    qr_data: d.qrData || `NXP-EDO|${d.edoNumber}|${d.blNumber}`,
  };
}

const DO_JSON_COLS = new Set(['containers']);

// ── SQL dùng chung ─────────────────────────────────────────────────────────────
async function insertRow(table, row, jsonCols, opts = {}) {
  const cols = Object.keys(row).filter((k) => row[k] !== undefined);
  const placeholders = cols.map((c) => (jsonCols.has(c) ? `CAST(:${c} AS jsonb)` : `:${c}`));
  const [rows] = await sequelize.query(
    `INSERT INTO ${table} (${cols.join(', ')}, created_at, updated_at)
     VALUES (${placeholders.join(', ')}, now(), now()) RETURNING *`,
    { replacements: row, transaction: opts.transaction },
  );
  return rows[0];
}

async function updateRow(table, id, patch, jsonCols, opts = {}) {
  const cols = Object.keys(patch).filter((k) => patch[k] !== undefined && k !== 'id');
  if (cols.length === 0) return null;
  const sets = cols.map((c) => (jsonCols.has(c) ? `${c} = CAST(:${c} AS jsonb)` : `${c} = :${c}`));
  const [rows] = await sequelize.query(
    `UPDATE ${table} SET ${sets.join(', ')}, updated_at = now() WHERE id = :__id RETURNING *`,
    { replacements: { ...patch, __id: id }, transaction: opts.transaction },
  );
  return rows[0] || null;
}

// ── API cho B/L ────────────────────────────────────────────────────────────────
async function listBls() {
  const [rows] = await sequelize.query(`SELECT * FROM bills_of_lading ORDER BY COALESCE(issue_date, created_at) DESC`);
  return rows.map(blFromRow);
}

async function findBl(idOrNumber) {
  const raw = String(idOrNumber || '').trim();
  if (!raw) return null;
  const [rows] = await sequelize.query(
    `SELECT * FROM bills_of_lading
      WHERE id = :raw OR upper(regexp_replace(bl_number, '\\s+', '', 'g')) = :clean
      ORDER BY (status = 'CANCELLED'), created_at DESC LIMIT 1`,
    { replacements: { raw, clean: norm(raw) } },
  );
  return blFromRow(rows[0]);
}

async function insertBl(bl, opts) {
  const row = blToRow({ ...bl, id: bl.id || randomUUID() });
  return blFromRow(await insertRow('bills_of_lading', row, BL_JSON_COLS, opts));
}

async function updateBl(id, patch, opts) {
  const full = blToRow({ id, ...patch });
  // Chỉ cập nhật những khóa đã truyền vào, không ghi đè cột còn lại bằng giá trị mặc định
  const keyMap = {
    blNumber: 'bl_number', direction: 'direction', blType: 'bl_type', voyageNumber: 'voyage_number', vesselName: 'vessel_name',
    pol: 'pol', pod: 'pod', shipper: 'shipper_name', consignee: 'consignee_name', notifyParty: 'notify_party',
    cargoDescription: 'cargo_description', packageCount: 'package_count', packageUnit: 'package_unit',
    grossWeightKg: 'total_gross_weight', measurementCbm: 'measurement_cbm', freightStatus: 'freight_status',
    freightAmountUsd: 'freight_amount_usd', freightPaid: 'freight_paid', freightPaymentStatus: 'freight_payment_status',
    localChargesStatus: 'local_charges_status', status: 'status', sourceType: 'source_type', sourceReference: 'source_reference',
    attachedFiles: 'attached_files', containers: 'containers', consigneeHold: 'consignee_hold',
    consigneeHoldReason: 'consignee_hold_reason', isCustomsHold: 'is_customs_hold', isCarrierHold: 'is_carrier_hold',
    holdReason: 'liner_hold_reason', inManifest: 'in_manifest',
  };
  const out = {};
  for (const [k, col] of Object.entries(keyMap)) {
    if (k in patch) out[col] = full[col];
  }
  if ('localChargesStatus' in patch) out.local_charges_paid = patch.localChargesStatus === 'PAID';
  if ('isCarrierHold' in patch) out.liner_hold = Boolean(patch.isCarrierHold);
  return blFromRow(await updateRow('bills_of_lading', id, out, BL_JSON_COLS, opts));
}

// ── API cho e-D/O ──────────────────────────────────────────────────────────────
async function listDos() {
  const [rows] = await sequelize.query(`SELECT * FROM delivery_orders ORDER BY COALESCE(issued_date, created_at) DESC`);
  return rows.map(doFromRow);
}

async function findDo(idOrNumber) {
  const raw = String(idOrNumber || '').trim();
  if (!raw) return null;
  const [rows] = await sequelize.query(
    `SELECT * FROM delivery_orders WHERE id::text = :raw OR do_number = :raw LIMIT 1`,
    { replacements: { raw } },
  );
  return doFromRow(rows[0]);
}

async function insertDo(d, opts) {
  const row = doToRow({ ...d, id: d.id || randomUUID() });
  return doFromRow(await insertRow('delivery_orders', row, DO_JSON_COLS, opts));
}

async function updateDo(id, patch, opts) {
  const keyMap = {
    validUntil: 'valid_until', status: 'status', notes: 'notes', cancelledAt: 'cancelled_at',
    cancelReason: 'cancel_reason', cancelledBy: 'cancelled_by', containers: 'containers', emptyReturnDepot: 'empty_return_depot',
  };
  const full = doToRow({ id, ...patch });
  const out = {};
  for (const [k, col] of Object.entries(keyMap)) {
    if (k in patch) out[col] = full[col];
  }
  return doFromRow(await updateRow('delivery_orders', id, out, DO_JSON_COLS, opts));
}

async function nextDoNumber(prefix = 'EDO') {
  const year = new Date().getFullYear();
  const [rows] = await sequelize.query(`SELECT nextval('delivery_order_number_seq') AS n`).catch(() => [[]]);
  const n = rows[0]?.n || Date.now().toString().slice(-6);
  return `${prefix}-${year}-${String(n).padStart(5, '0')}`;
}

module.exports = {
  sequelize,
  norm,
  fromDb,
  toDb,
  listBls,
  findBl,
  insertBl,
  updateBl,
  listDos,
  findDo,
  insertDo,
  updateDo,
  nextDoNumber,
};

'use strict';

/**
 * Đối chiếu vận đơn với container thực tế tại cảng, và cho Transport company xem vận đơn của mình.
 *
 * Nguồn thực tế: bảng containers của cảng (seal, khối lượng, trạng thái vào bãi/dỡ/rời bãi) và
 * export_booking_containers (xếp tàu). Chỉ đọc, không sửa dữ liệu.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const repo = require('../shipping-line/shippingLine.repository');
const { getDocumentEvents } = require('../document-workflow/documentEvents');

function httpError(status, message) {
  const err = new Error(message);
  err.statusCode = status;
  return err;
}

const norm = (v) => String(v || '').replace(/\s+/g, ' ').trim().toLowerCase();
const normNo = (v) => String(v || '').replace(/\s+/g, '').toUpperCase();
const WEIGHT_TOLERANCE = 0.05; // khối lượng cảng (gross) có thể lệch VGM tới 5%

const STATE_LABEL = {
  EXPORT: { NOT_FOUND: 'Chưa có trong hệ thống cảng', EXPECTED: 'Chưa vào cảng', AT_PORT: 'Đã vào cảng', LOADED: 'Đã xếp tàu' },
  IMPORT: { NOT_FOUND: 'Chưa có trong hệ thống cảng', EXPECTED: 'Chưa về cảng', DISCHARGED: 'Đã dỡ, đang ở bãi', RELEASED: 'Đã giao khỏi bãi' },
};

function stateOf(direction, port, loaded) {
  if (!port) return 'NOT_FOUND';
  const status = String(port.status || '');
  const left = Boolean(port.left_at) || ['gate_out', 'left', 'released', 'loaded'].includes(status);
  if (direction === 'EXPORT') {
    if (loaded || left) return 'LOADED';
    return ['gate_in', 'in_yard', 'reserved'].includes(status) ? 'AT_PORT' : 'EXPECTED';
  }
  if (left) return 'RELEASED';
  return ['discharged', 'in_yard'].includes(status) ? 'DISCHARGED' : 'EXPECTED';
}

async function containerStates(bl) {
  const direction = String(bl.direction).toUpperCase() === 'IMPORT' ? 'IMPORT' : 'EXPORT';
  const wanted = (bl.containers || []).map((c) => normNo(c.containerNumber || c.containerNo)).filter(Boolean);
  const [ports, loadedRows] = wanted.length
    ? await Promise.all([
      sequelize.query(
        `SELECT DISTINCT ON (upper(container_no)) upper(container_no) AS no, seal_no, gross_weight_kg, status::text AS status, arrived_at, left_at
           FROM containers WHERE upper(container_no) IN (:nos) ORDER BY upper(container_no), updated_at DESC`,
        { replacements: { nos: wanted }, type: QueryTypes.SELECT },
      ),
      sequelize.query(
        `SELECT upper(container_no) AS no, status, loaded_at, gate_in_at FROM export_booking_containers WHERE upper(container_no) IN (:nos)`,
        { replacements: { nos: wanted }, type: QueryTypes.SELECT },
      ),
    ])
    : [[], []];
  const portBy = new Map(ports.map((p) => [p.no, p]));
  const loadedBy = new Map(loadedRows.map((p) => [p.no, p]));

  return (bl.containers || []).map((c) => {
    const no = normNo(c.containerNumber || c.containerNo);
    const port = portBy.get(no) || null;
    const booking = loadedBy.get(no) || null;
    const state = stateOf(direction, port, booking?.status === 'Loaded' || Boolean(booking?.loaded_at));
    const issues = [];
    const blSeal = String(c.sealNumber || c.sealNo || '').trim();
    if (port && blSeal && port.seal_no && normNo(port.seal_no) !== normNo(blSeal)) issues.push(`Seal trên B/L (${blSeal}) khác seal tại cảng (${port.seal_no}).`);
    const vgm = Number(c.grossWeightKg || c.vgmKg || 0);
    const gross = Number(port?.gross_weight_kg || 0);
    if (port && vgm > 0 && gross > 0 && Math.abs(gross - vgm) / vgm > WEIGHT_TOLERANCE) issues.push(`Khối lượng B/L ${vgm} kg lệch khối lượng tại cảng ${gross} kg.`);
    return {
      containerNumber: no, sealNumber: blSeal || null, vgmKg: vgm || null,
      state, stateLabel: STATE_LABEL[direction][state],
      port: port && { sealNumber: port.seal_no, grossWeightKg: gross || null, status: port.status, arrivedAt: port.arrived_at, leftAt: port.left_at },
      issues,
    };
  });
}

/** Đối chiếu một B/L: từng container, lệch seal/khối lượng, và container chưa ở đúng chặng. */
async function reconcileBill(blNumber) {
  const bl = await repo.findBl(blNumber);
  if (!bl) throw httpError(404, 'Không tìm thấy vận đơn.');
  const direction = String(bl.direction).toUpperCase() === 'IMPORT' ? 'IMPORT' : 'EXPORT';
  const containers = await containerStates(bl);
  const done = direction === 'EXPORT' ? ['LOADED'] : ['DISCHARGED', 'RELEASED'];
  const summary = {
    total: containers.length,
    onTrack: containers.filter((c) => done.includes(c.state)).length,
    waiting: containers.filter((c) => ['EXPECTED', 'AT_PORT'].includes(c.state)).length,
    notFound: containers.filter((c) => c.state === 'NOT_FOUND').length,
    withIssues: containers.filter((c) => c.issues.length).length,
  };
  return {
    blNumber: bl.blNumber, direction, status: bl.status, voyageNumber: bl.voyageNumber, vesselName: bl.vesselName, shipper: bl.shipper, consignee: bl.consignee,
    containers, summary, matched: summary.total > 0 && summary.withIssues === 0 && summary.notFound === 0,
  };
}

/** Danh sách B/L (đã nhập hoặc phát hành) cho Hãng tàu chọn để đối chiếu. */
async function listBills() {
  const bls = await repo.listBls();
  return bls.filter((b) => b.status !== 'CANCELLED').map((b) => ({
    blNumber: b.blNumber, direction: String(b.direction).toUpperCase(), status: b.status, voyageNumber: b.voyageNumber, shipper: b.shipper, consignee: b.consignee,
    containerCount: (b.containers || []).length,
  }));
}

// ── Phía Transport company ─────────────────────────────────────────────────────
async function companyOf(user) {
  const [row] = await sequelize.query(
    `SELECT c.id, c.company_name, c.tax_code FROM carrier_users cu JOIN carriers c ON c.id = cu.carrier_id WHERE cu.user_id = :id LIMIT 1`,
    { replacements: { id: user.id }, type: QueryTypes.SELECT },
  );
  if (!row) throw httpError(403, 'Tài khoản chưa thuộc công ty vận tải nào.');
  return row;
}

const belongsTo = (company, text) => {
  const t = norm(text);
  if (!t) return false;
  const name = norm(company.company_name);
  return t === name || (name.length > 5 && (t.includes(name) || name.includes(t))) || Boolean(company.tax_code && t.includes(String(company.tax_code).toLowerCase()));
};

async function myBills(user) {
  const company = await companyOf(user);
  const bls = await repo.listBls();
  return bls
    .filter((b) => b.status !== 'CANCELLED' && b.status !== 'DRAFT')
    .filter((b) => (String(b.direction).toUpperCase() === 'EXPORT' ? belongsTo(company, b.shipper) : belongsTo(company, b.consignee)))
    .map((b) => ({
      blNumber: b.blNumber, direction: String(b.direction).toUpperCase(), status: b.status, blType: b.blType, voyageNumber: b.voyageNumber, vesselName: b.vesselName,
      pol: b.pol, pod: b.pod, shipper: b.shipper, consignee: b.consignee, containerCount: (b.containers || []).length, issueDate: b.issueDate,
      freightStatus: b.freightStatus, edoIssued: Boolean(b.edoIssued),
    }));
}

async function myBill(user, blNumber) {
  const company = await companyOf(user);
  const bl = await repo.findBl(blNumber);
  const direction = String(bl?.direction).toUpperCase();
  const mine = bl && bl.status !== 'CANCELLED' && bl.status !== 'DRAFT' && (direction === 'EXPORT' ? belongsTo(company, bl.shipper) : belongsTo(company, bl.consignee));
  if (!mine) throw httpError(404, 'Không tìm thấy vận đơn.');
  const containers = await containerStates(bl);
  const events = await getDocumentEvents(direction, direction === 'EXPORT' ? (bl.bookingNo || bl.blNumber) : bl.blNumber).catch(() => []);
  return {
    blNumber: bl.blNumber, direction, status: bl.status, blType: bl.blType, voyageNumber: bl.voyageNumber, vesselName: bl.vesselName, pol: bl.pol, pod: bl.pod,
    shipper: bl.shipper, consignee: bl.consignee, notifyParty: bl.notifyParty, cargoDescription: bl.cargoDescription, grossWeightKg: bl.grossWeightKg,
    freightStatus: bl.freightStatus, issueDate: bl.issueDate, containers: containers.map(({ port, issues, ...c }) => ({ ...c, hasIssues: issues.length > 0 })), events,
  };
}

module.exports = { reconcileBill, listBills, myBills, myBill };

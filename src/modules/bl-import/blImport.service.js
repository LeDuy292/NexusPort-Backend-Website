'use strict';

/**
 * Phát hành vận đơn (B/L) từ bản khai hàng hóa của Hãng tàu.
 *
 * Bản khai (shipping_manifests) là nguồn dữ liệu: mỗi bản khai gồm các vận đơn kèm container. Hãng tàu phát hành từng vận đơn:
 *  - Chiều xuất: tạo B/L chính thức, tạo pre-alert cho đầu nhập, báo cho chủ hàng (shipper).
 *  - Chiều nhập: mở hồ sơ hàng nhập (qua pre-alert, dùng chung luồng Arrival Notice/D/O), báo "hàng sắp về" cho người nhận.
 *
 * Điều kiện để phát hành:
 *  - Số vận đơn đúng định dạng và chưa được phát hành.
 *  - Người gửi (xuất) hoặc người nhận (nhập) phải là công ty đã đăng ký trong hệ thống.
 *  - Chiều xuất: mã booking phải là booking thật của đúng chủ hàng, đúng hãng tàu, đúng chuyến, còn hiệu lực,
 *    và mọi container trên vận đơn phải nằm trong booking đó.
 *  - Vận đơn phải có container, số container đúng chuẩn ISO 6346.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const shippingLineService = require('../shipping-line/shippingLine.service');
const repo = require('../shipping-line/shippingLine.repository');
const importDocuments = require('../import-documents/importDocuments.service');
const notify = require('../document-workflow/companyNotify');

function httpError(status, message) {
  const err = new Error(message);
  err.statusCode = status;
  return err;
}

const normNo = (v) => String(v || '').replace(/\s+/g, '').toUpperCase();
const BL_TYPES = ['Original', 'TelexRelease', 'SeawayBill'];
const FREIGHT = ['Prepaid', 'Collect'];
const BL_NUMBER_FORMAT = /^[A-Z0-9][A-Z0-9\-/]{5,34}$/;
const BL_NUMBER_HINT = 'Số vận đơn không hợp lệ: cần 6–35 ký tự gồm chữ, số, dấu - hoặc /.';
const CLOSED_BOOKING = ['Cancelled', 'Rejected', 'BlIssued'];
const needLine = (actor) => { if (actor.role !== 'Shipping Line') throw httpError(403, 'Chỉ Hãng tàu được phát hành vận đơn.'); };

async function loadManifest(manifestId, actor) {
  needLine(actor);
  const [row] = await sequelize.query('SELECT id, payload FROM shipping_manifests WHERE id = :id AND owner_id = :owner', {
    replacements: { id: manifestId, owner: actor.id }, type: QueryTypes.SELECT,
  });
  if (!row) throw httpError(404, 'Không tìm thấy bản khai.');
  return row.payload;
}

const toContainers = (bill) => (bill.containers || []).map((c) => ({
  containerNumber: String(c.containerNumber || '').trim().toUpperCase(), sealNumber: c.sealNumber || '', isoType: c.isoType || '',
  grossWeightKg: Number(c.grossWeightKg) || 0, temperature: c.temperature ?? null,
}));

/**
 * Mã booking phải là booking thật của đúng chủ hàng, thuộc hãng tàu này, đúng chuyến tàu, còn hiệu lực,
 * và mọi container trên vận đơn phải nằm trong danh sách container của booking đó.
 */
async function checkBooking({ bookingNo, company, actor, voyageNumber, containers }) {
  const no = String(bookingNo || '').trim();
  if (!no) throw httpError(400, 'Cần nhập mã booking của chủ hàng.');
  const [booking] = await sequelize.query(
    `SELECT b.id, b.booking_no, b.status, b.carrier_id, b.line_user_id, v.voyage_number
       FROM export_bookings b JOIN export_voyages v ON v.id = b.voyage_id WHERE upper(b.booking_no) = upper(:no) LIMIT 1`,
    { replacements: { no }, type: QueryTypes.SELECT },
  );
  if (!booking) throw httpError(400, `Mã booking ${no} không tồn tại trong hệ thống.`);
  const sameLine = booking.line_user_id === actor.id || (await sequelize.query(
    'SELECT 1 FROM shipping_line_accounts a JOIN shipping_line_accounts me ON me.shipping_line_id = a.shipping_line_id WHERE me.user_id = :me AND a.user_id = :owner LIMIT 1',
    { replacements: { me: actor.id, owner: booking.line_user_id }, type: QueryTypes.SELECT },
  )).length > 0;
  if (!sameLine) throw httpError(400, `Booking ${no} không thuộc hãng tàu của bạn.`);
  if (booking.carrier_id !== company.id) throw httpError(400, `Booking ${no} của công ty khác, không khớp với chủ hàng trên vận đơn.`);
  if (String(booking.voyage_number).toUpperCase() !== String(voyageNumber).toUpperCase()) {
    throw httpError(400, `Booking ${no} đặt cho chuyến ${booking.voyage_number}, không khớp chuyến ${voyageNumber} của bản khai.`);
  }
  if (CLOSED_BOOKING.includes(booking.status)) throw httpError(400, `Booking ${no} đang ở trạng thái không phát hành được vận đơn (${booking.status}).`);
  const declared = new Set((await sequelize.query(
    "SELECT upper(container_no) AS no FROM export_booking_containers WHERE export_booking_id = :id AND status NOT IN ('Excluded', 'Rolled')",
    { replacements: { id: booking.id }, type: QueryTypes.SELECT },
  )).map((r) => r.no));
  if (declared.size === 0) throw httpError(400, `Booking ${no} chưa khai danh sách container.`);
  const outside = containers.filter((c) => !declared.has(c.containerNumber)).map((c) => c.containerNumber);
  if (outside.length) throw httpError(400, `Container ${outside.join(', ')} không nằm trong booking ${no}.`);
  return booking;
}

/** Các vận đơn trong bản khai kèm trạng thái phát hành và lưu ý trước khi phát hành. */
async function listBills(manifestId, actor) {
  const manifest = await loadManifest(manifestId, actor);
  const direction = String(manifest.direction).toUpperCase() === 'IMPORT' ? 'IMPORT' : 'EXPORT';
  const existing = new Map((await repo.listBls()).filter((b) => b.status !== 'CANCELLED').map((b) => [normNo(b.blNumber), b]));
  const bills = [];
  for (const bill of manifest.bills || []) {
    const found = existing.get(normNo(bill.blNumber));
    const containers = toContainers(bill);
    const notes = [];
    if (!BL_NUMBER_FORMAT.test(normNo(bill.blNumber))) notes.push(BL_NUMBER_HINT);
    if (!String(bill.shipper || '').trim()) notes.push('Thiếu người gửi hàng.');
    if (!String(bill.consignee || '').trim()) notes.push('Thiếu người nhận hàng.');
    if (containers.length === 0) notes.push('Vận đơn chưa có container.');
    const partyName = direction === 'EXPORT' ? bill.shipper : bill.consignee;
    const company = partyName ? await notify.findCompany(partyName) : null;
    if (partyName && !company) notes.push(direction === 'EXPORT' ? 'Người gửi hàng chưa là công ty đã đăng ký trong hệ thống.' : 'Người nhận hàng chưa là công ty đã đăng ký trong hệ thống.');
    const companyName = company
      ? (await sequelize.query('SELECT company_name FROM carriers WHERE id = :id LIMIT 1', { replacements: { id: company.id }, type: QueryTypes.SELECT }))[0]?.company_name
      : null;
    bills.push({
      blNumber: bill.blNumber, shipper: bill.shipper, consignee: bill.consignee, containerCount: containers.length,
      issued: Boolean(found && found.status !== 'DRAFT'), blStatus: found?.status || null, companyName,
      notes, canIssue: notes.length === 0 && !(found && found.status !== 'DRAFT'),
    });
  }
  return { manifestId, manifestNumber: manifest.manifestNumber, direction, voyageNumber: manifest.voyageNumber, status: manifest.status, bills };
}

/**
 * Phát hành một vận đơn trong bản khai.
 * options: { blType, freightTerms, notifyParty, bookingNo, cargoDescription }
 */
async function issue(manifestId, blNumber, options = {}, actor) {
  const manifest = await loadManifest(manifestId, actor);
  if (manifest.status === 'DRAFT') throw httpError(409, 'Bản khai còn là bản nháp. Hoàn tất bản khai trước khi phát hành vận đơn.');
  const bill = (manifest.bills || []).find((b) => normNo(b.blNumber) === normNo(blNumber));
  if (!bill) throw httpError(404, 'Không tìm thấy vận đơn trong bản khai.');
  if (!BL_TYPES.includes(options.blType)) throw httpError(400, 'Hãy chọn hình thức B/L.');
  if (!FREIGHT.includes(options.freightTerms)) throw httpError(400, 'Hãy chọn điều kiện cước.');
  if (!BL_NUMBER_FORMAT.test(normNo(bill.blNumber))) throw httpError(400, BL_NUMBER_HINT);
  if (!String(bill.shipper || '').trim() || !String(bill.consignee || '').trim()) throw httpError(400, 'Vận đơn cần có người gửi và người nhận hàng.');
  const containers = toContainers(bill);
  if (containers.length === 0) throw httpError(400, 'Vận đơn chưa có container nên không thể phát hành.');
  for (const c of containers) {
    const check = shippingLineService.validateContainerNumberISO(c.containerNumber);
    if (!check.isValid) throw httpError(400, `Container ${c.containerNumber || '(trống)'}: ${check.reason}`);
  }

  const direction = String(manifest.direction).toUpperCase() === 'IMPORT' ? 'IMPORT' : 'EXPORT';
  // Công ty trên vận đơn phải là công ty đã đăng ký (chủ hàng với hàng xuất, người nhận với hàng nhập)
  const partyName = direction === 'EXPORT' ? bill.shipper : bill.consignee;
  const registered = await notify.findCompany(partyName);
  if (!registered) {
    throw httpError(400, `${direction === 'EXPORT' ? 'Người gửi hàng' : 'Người nhận hàng'} "${partyName}" chưa là công ty đã đăng ký trong hệ thống nên chưa thể phát hành.`);
  }
  if (direction === 'EXPORT') {
    await checkBooking({ bookingNo: options.bookingNo, company: registered, actor, voyageNumber: manifest.voyageNumber, containers });
  }

  const found = (await repo.listBls()).find((b) => normNo(b.blNumber) === normNo(bill.blNumber) && b.status !== 'CANCELLED');
  if (found && found.status !== 'DRAFT') throw httpError(409, `Vận đơn ${bill.blNumber} đã được phát hành.`);

  const voyage = (await shippingLineService.getVoyages({})).find((v) => String(v.voyageNumber).toUpperCase() === String(manifest.voyageNumber).toUpperCase());
  const totalVgm = containers.reduce((s, c) => s + c.grossWeightKg, 0);
  const cargo = options.cargoDescription || [...new Set((bill.containers || []).map((c) => c.commodity).filter(Boolean))].join('; ') || 'Theo bản khai hàng hóa';
  const bl = {
    blNumber: String(bill.blNumber).trim().toUpperCase(), bookingNo: options.bookingNo ? String(options.bookingNo).trim() : null, voyageNumber: manifest.voyageNumber, direction,
    blType: options.blType, freightStatus: options.freightTerms === 'Collect' ? 'COLLECT' : 'PREPAID', shipper: bill.shipper, consignee: bill.consignee,
    notifyParty: options.notifyParty || '', cargoDescription: cargo, grossWeightKg: totalVgm, containers,
    vesselName: manifest.vesselName || voyage?.vesselName || '', pol: voyage?.pol || '', pod: voyage?.pod || '', etd: voyage?.etd || null, eta: voyage?.eta || null,
    companyId: registered.id,
  };
  const eta = bl.eta ? new Date(String(bl.eta).replace(' ', 'T') + '+07:00') : null;

  if (direction === 'IMPORT') {
    // Chiều nhập: mở hồ sơ qua pre-alert để dùng chung luồng đối chiếu, Arrival Notice và D/O
    const alertId = await createPreAlert(bl, eta);
    if (!alertId) throw httpError(409, 'Pre-alert cho số B/L này đã tồn tại.');
    const file = await importDocuments.receivePreAlert(alertId, actor);
    await importDocuments.assignConsignee(file.id, bl.companyId, actor);
    await notify.notifyCargoArriving(bl, { companyId: bl.companyId });
  } else {
    const typeCode = bl.blType === 'TelexRelease' ? 'TELEX_RELEASE' : bl.blType === 'SeawayBill' ? 'SEAWAY_BILL' : 'ORIGINAL';
    const payload = {
      blNumber: bl.blNumber, direction, voyageNumber: bl.voyageNumber, vesselName: bl.vesselName || undefined, pol: bl.pol || undefined, pod: bl.pod || undefined,
      shipper: bl.shipper, consignee: bl.consignee, notifyParty: bl.notifyParty || undefined, cargoDescription: bl.cargoDescription,
      grossWeightKg: bl.grossWeightKg || undefined, freightStatus: bl.freightStatus, blType: typeCode, containers: bl.containers,
      sourceType: 'MANIFEST', sourceReference: bl.bookingNo || manifest.manifestNumber,
    };
    if (found) await repo.updateBl(found.id, { ...payload, status: 'ISSUED' });
    else await shippingLineService.createBillOfLading(payload, actor.username);
    await createPreAlert(bl, eta);
    await notify.notifyBlIssuedExport(bl, { companyId: bl.companyId });
  }
  return { blNumber: bl.blNumber, direction, notified: true, companyMatched: true };
}

async function createPreAlert(bl, eta) {
  const [row] = await sequelize.query(
    `INSERT INTO shipment_pre_alerts(export_booking_id, booking_no, bl_number, vessel_name, voyage_number, port_of_loading, port_of_discharge, etd, eta, bl_type, freight_terms, shipper, consignee, notify_party, containers, payload)
     VALUES(gen_random_uuid(), :bookingNo, :bl, :vessel, :voyage, :pol, :pod, :etd, :eta, :blType, :terms, :shipper, :consignee, :notify, CAST(:containers AS jsonb), CAST(:payload AS jsonb))
     ON CONFLICT (bl_number) DO NOTHING RETURNING id`,
    {
      replacements: {
        bookingNo: bl.bookingNo || `MNF-${bl.blNumber}`, bl: bl.blNumber, vessel: bl.vesselName, voyage: bl.voyageNumber, pol: bl.pol, pod: bl.pod,
        etd: bl.etd ? new Date(String(bl.etd).replace(' ', 'T') + '+07:00') : null, eta,
        blType: bl.blType, terms: bl.freightStatus === 'COLLECT' ? 'Collect' : 'Prepaid', shipper: bl.shipper, consignee: bl.consignee, notify: bl.notifyParty,
        containers: JSON.stringify(bl.containers.map((c) => ({ containerNumber: c.containerNumber, sealNumber: c.sealNumber, vgmKg: c.grossWeightKg, isoType: c.isoType }))),
        payload: JSON.stringify({ cargoDescription: bl.cargoDescription, source: 'MANIFEST' }),
      },
      type: QueryTypes.SELECT,
    },
  );
  return row?.id || null;
}

module.exports = { listBills, issue };

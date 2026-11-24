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
 *  - Chiều xuất: vận đơn là căn cứ khai báo dịch vụ ePort (theo số vận đơn); container phải đã được chủ hàng khai báo
 *    và không nằm trên vận đơn xuất khác còn hiệu lực.
 *  - Vận đơn phải có container, số container đúng chuẩn ISO 6346.
 */

const crypto = require('crypto');
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

const vnTime = (v) => (v ? new Date(String(v).includes('T') ? v : String(v).replace(' ', 'T') + '+07:00') : null);

const NO_ID = '00000000-0000-0000-0000-000000000000';

/**
 * Gắn container của vận đơn vào nguồn khai báo ePort (bỏ qua container đã có).
 * Container đang thuộc vận đơn khác còn hiệu lực thì chặn; container chưa được chủ hàng khai báo thì báo lại.
 * Sau đó cập nhật số lượng và loại container.
 */
async function attachContainers(q, { bookingId, companyId, companyName, actorId, containers }) {
  const busy = await q(
    `SELECT upper(c.container_no) AS no, b.booking_no FROM export_booking_containers c JOIN export_bookings b ON b.id = c.export_booking_id
      WHERE upper(c.container_no) IN (:nos) AND c.status NOT IN ('Excluded', 'Rolled') AND b.status NOT IN (:closed) AND b.id <> :booking`,
    { nos: containers.map((c) => c.containerNumber), closed: CLOSED_BOOKING, booking: bookingId || NO_ID },
  );
  if (busy.length) {
    throw httpError(400, `Container đã thuộc vận đơn khác còn hiệu lực: ${busy.map((r) => `${r.no} (${r.booking_no})`).join(', ')}.`);
  }
  const added = [];
  const undeclared = [];
  for (const c of containers) {
    const [already] = await q(
      "SELECT 1 FROM export_booking_containers WHERE export_booking_id = :booking AND upper(container_no) = :no AND status NOT IN ('Excluded', 'Rolled') LIMIT 1",
      { booking: bookingId, no: c.containerNumber },
    );
    if (already) continue;
    let [decl] = await q(
      `SELECT ct.id AS container_id, d.id AS declaration_id, d.cargo_type
         FROM containers ct JOIN container_declarations d ON d.container_id = ct.id AND d.carrier_id = :company
        WHERE upper(ct.container_no) = :no ORDER BY d.created_at DESC LIMIT 1`,
      { company: companyId, no: c.containerNumber },
    );
    const vgmVal = Number(c.grossWeightKg) > 0 ? Number(c.grossWeightKg) : 24000;
    const sealVal = String(c.sealNumber || '').trim() || `SL-${String(c.containerNumber).slice(-5)}`;
    if (!decl) {
      // Container đã qua kiểm tra của hãng tàu: lập thiết bị & khai báo từ dữ liệu hãng tàu đã nhập.
      let [contRow] = await q('SELECT id FROM containers WHERE upper(container_no) = :no LIMIT 1', { no: c.containerNumber });
      if (!contRow) {
        const iso = String(c.isoType || '40HC').trim().toUpperCase();
        const isReefer = /^..R|RF|RH|R0|R1/i.test(iso) || c.temperature !== null;
        const [typeRow] = await q(
          'SELECT id FROM container_types ORDER BY (upper(code) = :iso) DESC, (size::text = :sz) DESC, code LIMIT 1',
          { iso, sz: iso.startsWith('2') ? '20FT' : '40FT' },
        );
        if (typeRow) {
          [contRow] = await q(
            `INSERT INTO containers (id, container_no, container_type_id, carrier_id, status, is_reefer)
             VALUES (gen_random_uuid(), :no, :typeId, :company, 'expected', :isReefer)
             ON CONFLICT (container_no) DO UPDATE SET updated_at = now() RETURNING id`,
            { no: c.containerNumber, typeId: typeRow.id, company: companyId, isReefer },
          );
        }
      }
      if (contRow) {
        const cargoType = (/^..R|RF|RH|R0|R1/i.test(String(c.isoType || '')) || c.temperature !== null) ? 'reefer' : 'general';
        [decl] = await q(
          `INSERT INTO container_declarations (id, container_id, declared_by, carrier_id, load_status, cargo_type, gross_weight_kg, customer_name, seal_number)
           VALUES (gen_random_uuid(), :cid, :by, :company, 'full', CAST(:cargo AS cargo_type), :kg, :name, :seal)
           RETURNING container_id, id AS declaration_id, cargo_type`,
          { cid: contRow.id, by: actorId || null, company: companyId, cargo: cargoType, kg: vgmVal, name: companyName || null, seal: sealVal },
        );
      }
    }
    if (!decl) {
      console.warn(`[bl-import] không gắn được container ${c.containerNumber} vào booking ${bookingId}: chưa đăng ký thiết bị`);
      undeclared.push(c.containerNumber);
      continue;
    }
    const [row] = await q(
      `INSERT INTO export_booking_containers (id, export_booking_id, container_id, declaration_id, container_no, type_code, seal_number, vgm_kg, cargo_type, status)
       VALUES (gen_random_uuid(), :booking, :cid, :did, :no, :type, :seal, :vgm, :cargoType, 'Expected') RETURNING id`,
      { booking: bookingId, cid: decl.container_id, did: decl.declaration_id, no: c.containerNumber, type: c.isoType || '40HC',
        seal: sealVal, vgm: vgmVal, cargoType: decl.cargo_type || 'general' },
    );
    added.push(row.id);
  }
  const [count] = await q(
    `SELECT count(*)::int AS n, array_agg(DISTINCT type_code) AS types FROM export_booking_containers
      WHERE export_booking_id = :booking AND status NOT IN ('Excluded', 'Rolled')`,
    { booking: bookingId },
  );
  if (count.n > 50) throw httpError(400, 'Một vận đơn tối đa 50 container.');
  if (count.n > 0) {
    await q(
      'UPDATE export_bookings SET quantity = :n, total_qty = :n, container_type_code = :type, updated_at = now() WHERE id = :booking RETURNING id',
      { n: count.n, type: count.types.length === 1 ? count.types[0] : 'MIXED', booking: bookingId },
    );
  }
  return { added, undeclared };
}

/** Chuyến xuất trong DB cho bản khai (tạo từ lịch chuyến của Hãng tàu nếu chưa có). */
async function ensureExportVoyage(q, { manifest, voyage, actor }) {
  let [exportVoyage] = await q(
    `SELECT v.id, v.line_user_id IN (SELECT user_id FROM shipping_line_accounts
                                      WHERE shipping_line_id = (SELECT shipping_line_id FROM shipping_line_accounts WHERE user_id = :me)
                                     UNION SELECT :me::uuid) AS mine
       FROM export_voyages v WHERE upper(v.voyage_number) = upper(:no) LIMIT 1`,
    { no: manifest.voyageNumber, me: actor.id },
  );
  if (exportVoyage && !exportVoyage.mine) throw httpError(400, `Chuyến ${manifest.voyageNumber} thuộc hãng tàu khác.`);
  if (exportVoyage) return exportVoyage.id;
  if (!voyage) throw httpError(400, `Không tìm thấy lịch chuyến ${manifest.voyageNumber}.`);
  const etd = vnTime(voyage.etd);
  const cy = vnTime(voyage.cyCutOff);
  const vgm = vnTime(voyage.vgmCutOff);
  if (![etd, cy, vgm].every((d) => d && !Number.isNaN(d.getTime())) || !(cy < etd) || vgm > etd) {
    throw httpError(400, `Lịch chuyến ${manifest.voyageNumber} chưa hợp lệ (cần ETD, hạn hạ bãi và hạn VGM trước ETD) nên chưa phát hành được vận đơn.`);
  }
  [exportVoyage] = await q(
    `INSERT INTO export_voyages (id, line_user_id, vessel_name, voyage_number, port_of_loading, port_of_discharge, etd, eta, receive_from, cy_cutoff, vgm_cutoff, si_cutoff, status)
     VALUES (gen_random_uuid(), :me, :vessel, :no, :pol, :pod, :etd, NULL, :receiveFrom, :cy, :vgm, :vgm, 'Scheduled') RETURNING id`,
    { me: actor.id, vessel: voyage.vesselName || manifest.vesselName, no: manifest.voyageNumber, pol: voyage.pol, pod: voyage.pod,
      etd, receiveFrom: new Date(Math.min(Date.now(), cy.getTime() - 3600 * 1000)), cy, vgm },
  );
  return exportVoyage.id;
}

/**
 * Vận đơn xuất là căn cứ khai báo dịch vụ ePort (hạ bãi): tạo bản ghi nguồn ẩn mang chính số vận đơn,
 * gắn chuyến xuất và các container đã khai báo của chủ hàng. Hệ thống không dùng mã booking riêng.
 */
async function createBlSource({ blNumber, manifest, voyage, company, actor, containers, cargo }) {
  if (containers.length > 50) throw httpError(400, 'Một vận đơn tối đa 50 container.');
  return sequelize.transaction(async (transaction) => {
    const q = (sql, replacements) => sequelize.query(sql, { replacements, type: QueryTypes.SELECT, transaction });
    const voyageId = await ensureExportVoyage(q, { manifest, voyage, actor });
    let created = false;
    let [source] = await q('SELECT id, carrier_id FROM export_bookings WHERE upper(booking_no) = upper(:no) LIMIT 1 FOR UPDATE', { no: blNumber });
    if (source && source.carrier_id !== company.id) throw httpError(409, `Số vận đơn ${blNumber} đã được dùng cho chủ hàng khác.`);
    if (!source) {
      [source] = await q(
        `INSERT INTO export_bookings (id, voyage_id, booking_no, bl_number, container_type_code, quantity, commodity_description, status, created_by,
                                      carrier_id, line_user_id, shipper_name, port_of_discharge, total_qty, port_status, manifest_id)
         VALUES (gen_random_uuid(), :voyage, :no, :no, 'MIXED', :qty, :cargo, 'ContainersDeclared', :me, :company, :me, :shipper, :pod, :qty, 'Accepted', :m) RETURNING id`,
        { voyage: voyageId, no: blNumber, qty: Math.min(Math.max(containers.length, 1), 50), cargo, me: actor.id, company: company.id,
          shipper: company.company_name || null, pod: (voyage && voyage.pod) || null, m: manifest.id },
      );
      created = true;
    }
    const r = await attachContainers(q, { bookingId: source.id, companyId: company.id, companyName: company.company_name, actorId: actor.id, containers });
    return { sourceId: source.id, created, ...r };
  });
}

/**
 * Đồng bộ lại container của vận đơn xuất ĐÃ phát hành vào nguồn khai báo ePort
 * (dùng khi lúc phát hành container chưa gắn được, vd. thiếu trọng lượng hoặc chưa đăng ký thiết bị).
 */
async function syncContainers(manifestId, blNumber, actor) {
  const manifest = await loadManifest(manifestId, actor);
  if (String(manifest.direction).toUpperCase() === 'IMPORT') throw httpError(400, 'Chỉ áp dụng cho vận đơn xuất.');
  const bill = (manifest.bills || []).find((b) => normNo(b.blNumber) === normNo(blNumber));
  if (!bill) throw httpError(404, 'Không tìm thấy vận đơn trong bản khai.');
  const registered = await notify.findCompany(bill.shipper);
  if (!registered) throw httpError(400, `Người gửi hàng "${bill.shipper}" chưa là công ty đã đăng ký trong hệ thống.`);
  const containers = toContainers(bill);
  const [companyRow] = await sequelize.query('SELECT company_name FROM carriers WHERE id = :id LIMIT 1', { replacements: { id: registered.id }, type: QueryTypes.SELECT });
  const no = String(bill.blNumber).trim().toUpperCase();
  return sequelize.transaction(async (transaction) => {
    const q = (sql, replacements) => sequelize.query(sql, { replacements, type: QueryTypes.SELECT, transaction });
    const [source] = await q('SELECT id, carrier_id FROM export_bookings WHERE upper(booking_no) = :no LIMIT 1 FOR UPDATE', { no });
    if (!source) throw httpError(409, `Vận đơn ${no} chưa được phát hành nên chưa có nguồn khai báo ePort.`);
    if (source.carrier_id !== registered.id) throw httpError(409, `Số vận đơn ${no} đã được dùng cho chủ hàng khác.`);
    const r = await attachContainers(q, { bookingId: source.id, companyId: registered.id, companyName: companyRow && companyRow.company_name, actorId: actor.id, containers });
    return { blNumber: no, added: r.added.length, undeclaredContainers: r.undeclared };
  });
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
    const issuedAlready = Boolean(found && found.status !== 'DRAFT');
    if (direction === 'EXPORT' && containers.length && !issuedAlready) {
      // Container không được nằm trên vận đơn xuất / nguồn khai báo khác còn hiệu lực
      const busy = await sequelize.query(
        `SELECT upper(c.container_no) AS no, b.booking_no FROM export_booking_containers c JOIN export_bookings b ON b.id = c.export_booking_id
          WHERE upper(c.container_no) IN (:nos) AND c.status NOT IN ('Excluded', 'Rolled') AND b.status NOT IN (:closed) AND upper(b.booking_no) <> upper(:bl)`,
        { replacements: { nos: containers.map((c) => c.containerNumber), closed: CLOSED_BOOKING, bl: normNo(bill.blNumber) }, type: QueryTypes.SELECT },
      );
      if (busy.length) notes.push(`Container đã thuộc vận đơn khác còn hiệu lực: ${busy.map((r) => `${r.no} (${r.booking_no})`).join(', ')}.`);
    }
    // Cảnh báo (không chặn phát hành): container không gắn được vào nguồn ePort khi phát hành.
    // Chủ hàng không phải khai báo lại; chỉ cần container đã đăng ký thiết bị và có trọng lượng.
    const warnings = [];
    if (direction === 'EXPORT' && containers.length && !issuedAlready) {
      const registeredNos = new Set((await sequelize.query(
        'SELECT upper(container_no) AS no FROM containers WHERE upper(container_no) IN (:nos)',
        { replacements: { nos: containers.map((c) => String(c.containerNumber).toUpperCase()) }, type: QueryTypes.SELECT },
      )).map((r) => r.no));
      const unusable = containers
        .filter((c) => !registeredNos.has(String(c.containerNumber).toUpperCase()) || !(c.grossWeightKg > 0))
        .map((c) => c.containerNumber);
      if (unusable.length) {
        warnings.push(`Container chưa đăng ký thiết bị hoặc thiếu trọng lượng nên sẽ không dùng được để khai báo ePort: ${unusable.join(', ')}.`);
      }
    }
    bills.push({
      warnings,
      blNumber: bill.blNumber, shipper: bill.shipper, consignee: bill.consignee, containerCount: containers.length, containers,
      issuedBl: found && found.status !== 'DRAFT' ? {
        blType: found.blType, freightStatus: found.freightStatus, notifyParty: found.notifyParty, cargoDescription: found.cargoDescription,
        grossWeightKg: found.grossWeightKg, issueDate: found.issueDate, pol: found.pol, pod: found.pod, vesselName: found.vesselName,
      } : null,
      issued: Boolean(found && found.status !== 'DRAFT'), blStatus: found?.status || null, companyName,
      notes, canIssue: notes.length === 0 && !(found && found.status !== 'DRAFT'),
    });
  }
  return { manifestId, manifestNumber: manifest.manifestNumber, direction, voyageNumber: manifest.voyageNumber, status: manifest.status, bills };
}

/**
 * Phát hành một vận đơn trong bản khai.
 * options: { blType, freightTerms, notifyParty, cargoDescription }
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

  const found = (await repo.listBls()).find((b) => normNo(b.blNumber) === normNo(bill.blNumber) && b.status !== 'CANCELLED');
  if (found && found.status !== 'DRAFT') throw httpError(409, `Vận đơn ${bill.blNumber} đã được phát hành.`);

  const voyage = (await shippingLineService.getVoyages({ includeCompleted: true })).find((v) => String(v.voyageNumber).toUpperCase() === String(manifest.voyageNumber).toUpperCase());
  if (voyage) {
    const tienSa = /TIÊN SA|TIEN SA|VNDAD/i;
    const fits = direction === 'IMPORT' ? tienSa.test(String(voyage.pod || '')) : tienSa.test(String(voyage.pol || ''));
    if (!fits) {
      throw httpError(400, direction === 'IMPORT'
        ? `Chuyến ${voyage.voyageNumber} không dỡ hàng tại Cảng Tiên Sa (${voyage.pol} → ${voyage.pod}) nên không dùng cho bản khai nhập.`
        : `Chuyến ${voyage.voyageNumber} không xếp hàng tại Cảng Tiên Sa (${voyage.pol} → ${voyage.pod}) nên không dùng cho bản khai xuất.`);
    }
  }
  const totalVgm = containers.reduce((s, c) => s + c.grossWeightKg, 0);
  const cargo = options.cargoDescription || [...new Set((bill.containers || []).map((c) => c.commodity).filter(Boolean))].join('; ') || 'Theo bản khai hàng hóa';
  let source = null; // nguồn khai báo ePort của vận đơn xuất
  const bl = {
    blNumber: String(bill.blNumber).trim().toUpperCase(), voyageNumber: manifest.voyageNumber, direction,
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
      sourceType: 'MANIFEST', sourceReference: manifest.manifestNumber,
    };
    // Nguồn khai báo ePort mang chính số vận đơn (chủ hàng khai báo dịch vụ hạ bãi theo số vận đơn)
    const [companyRow] = await sequelize.query('SELECT company_name FROM carriers WHERE id = :id LIMIT 1', { replacements: { id: registered.id }, type: QueryTypes.SELECT });
    source = await createBlSource({
      blNumber: bl.blNumber, manifest, voyage, company: { ...registered, company_name: companyRow && companyRow.company_name }, actor, containers, cargo,
    });
    try {
      if (found) await repo.updateBl(found.id, { ...payload, status: 'ISSUED' });
      else await shippingLineService.createBillOfLading(payload, actor.username);
    } catch (err) {
      if (source.created) {
        await sequelize.query(
          'DELETE FROM export_booking_containers WHERE export_booking_id = :id; DELETE FROM export_bookings WHERE id = :id',
          { replacements: { id: source.sourceId } },
        ).catch(() => {});
      } else if (source.added.length) {
        await sequelize.query('DELETE FROM export_booking_containers WHERE id IN (:ids)', { replacements: { ids: source.added } }).catch(() => {});
      }
      throw err;
    }
    // Hàng xuất dỡ ở cảng khác: hồ sơ hàng nhập do đại lý cảng dỡ mở, không tạo pre-alert vào hồ sơ nhập của cảng này
    await notify.notifyBlIssuedExport(bl, { companyId: bl.companyId });
  }
  return {
    blNumber: bl.blNumber, direction, notified: true, companyMatched: true,
    undeclaredContainers: (source && source.undeclared) || [],
  };
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

module.exports = { listBills, issue, syncContainers };

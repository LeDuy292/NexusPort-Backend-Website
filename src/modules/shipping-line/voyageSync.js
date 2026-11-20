'use strict';

/**
 * Một danh sách chuyến tàu duy nhất.
 * - Chuyến xuất (rời Cảng Tiên Sa) có bản ghi trong export_voyages: nơi booking, ePort và cổng đọc dữ liệu.
 *   Bản ghi DB là chuẩn cho tuyến và các mốc giờ; danh sách chuyến của Hãng tàu lấy theo.
 * - Chuyến chỉ có trong DB (VD tạo từ booking) được đưa vào danh sách chuyến của Hãng tàu.
 * - Tạo / sửa chuyến xuất ở trang Tàu & Chuyến Tàu ghi luôn vào export_voyages.
 * - Chuyến nhập (đến Tiên Sa) chỉ nằm trong danh sách của Hãng tàu (không có booking xuất).
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

const TIEN_SA = /TIÊN SA|TIEN SA|VNDAD/i;
const isExportRoute = (v) => TIEN_SA.test(String(v?.pol || ''));

// Giờ VN dạng 'YYYY-MM-DD HH:mm' <-> Date
const toVn = (d) => (d ? new Date(new Date(d).getTime() + 7 * 3600 * 1000).toISOString().replace('T', ' ').slice(0, 16) : null);
const fromVn = (s) => (s ? new Date(String(s).includes('T') ? s : String(s).replace(' ', 'T') + '+07:00') : null);

const DB_TO_LINE_STATUS = { Scheduled: 'SCHEDULED', Loading: 'OPERATING', Closed: 'OPERATING', Departed: 'DEPARTED' };
const LINE_TO_DB_STATUS = (status) => (['DEPARTED', 'COMPLETED'].includes(status) ? 'Departed' : status === 'OPERATING' ? 'Loading' : 'Scheduled');

/**
 * Khóa "hãng tàu" của một tài khoản: shipping_line_id nếu có (các tài khoản cùng hãng dùng chung dữ liệu),
 * ngược lại dùng chính id tài khoản. Dùng để gắn chủ sở hữu cho tàu / chuyến và lọc theo hãng đang đăng nhập.
 */
async function lineKeyOfUser(userId) {
  if (!userId) return null;
  const [row] = await sequelize.query(
    `SELECT COALESCE((SELECT shipping_line_id::text FROM shipping_line_accounts WHERE user_id = :id LIMIT 1), :id::text) AS k`,
    { replacements: { id: userId }, type: QueryTypes.SELECT },
  );
  return row?.k || null;
}

async function lineKeyOfUsername(username) {
  const [user] = await sequelize.query('SELECT id FROM users WHERE username = :u LIMIT 1', { replacements: { u: username }, type: QueryTypes.SELECT });
  return lineKeyOfUser(user?.id);
}

/** Kéo chuyến xuất trong DB vào danh sách chuyến của Hãng tàu (thêm mới hoặc cập nhật tuyến / mốc giờ). */
async function syncFromDb(voyages, vessels) {
  const rows = await sequelize.query(
    `SELECT v.id, v.voyage_number, v.vessel_name, v.port_of_loading, v.port_of_discharge, v.etd, v.cy_cutoff, v.vgm_cutoff, v.status,
            COALESCE((SELECT a.shipping_line_id::text FROM shipping_line_accounts a WHERE a.user_id = v.line_user_id LIMIT 1), v.line_user_id::text) AS line_key
       FROM export_voyages v ORDER BY v.etd`,
    { type: QueryTypes.SELECT },
  );
  for (const r of rows) {
    const no = String(r.voyage_number).toUpperCase();
    const fromDb = {
      lineKey: r.line_key,
      dbVoyageId: r.id,
      pol: r.port_of_loading,
      pod: r.port_of_discharge,
      etd: toVn(r.etd),
      cyCutOff: toVn(r.cy_cutoff),
      vgmCutOff: toVn(r.vgm_cutoff),
    };
    const current = voyages.find((v) => String(v.voyageNumber).toUpperCase() === no);
    if (current) {
      Object.assign(current, fromDb);
      if (r.status === 'Departed' && !['DEPARTED', 'COMPLETED'].includes(current.status)) current.status = 'DEPARTED';
      continue;
    }
    const vessel = vessels.find((x) => String(x.vesselName).toUpperCase() === String(r.vessel_name).toUpperCase());
    voyages.push({
      id: r.id,
      voyageNumber: r.voyage_number,
      vesselId: vessel?.id || null,
      vesselName: r.vessel_name,
      ...fromDb,
      eta: null, // ETA tại Cảng Tiên Sa chưa được khai báo cho chuyến tạo từ booking
      ata: null,
      atd: null,
      status: DB_TO_LINE_STATUS[r.status] || 'SCHEDULED',
      terminal: 'Bến số 1 (TS-01) - Cảng Tiên Sa',
      remarks: 'Chuyến xuất đồng bộ từ booking',
      manifestCount: 0,
      containerCount: 0,
    });
  }
}

/** Ghi chuyến xuất mới vào export_voyages (cùng mã chuyến). Chuyến nhập bỏ qua. */
async function createInDb(voyage, username) {
  if (!isExportRoute(voyage)) return null;
  const [user] = await sequelize.query('SELECT id FROM users WHERE username = :u LIMIT 1', { replacements: { u: username }, type: QueryTypes.SELECT });
  if (!user) throw new Error('Không xác định được tài khoản Hãng tàu tạo chuyến');
  const cy = fromVn(voyage.cyCutOff);
  const [row] = await sequelize.query(
    `INSERT INTO export_voyages (id, line_user_id, vessel_name, voyage_number, port_of_loading, port_of_discharge, etd, eta, receive_from, cy_cutoff, vgm_cutoff, si_cutoff, status)
     VALUES (gen_random_uuid(), :me, :vessel, :no, :pol, :pod, :etd, NULL, :receiveFrom, :cy, :vgm, :vgm, 'Scheduled') RETURNING id`,
    {
      replacements: {
        me: user.id, vessel: voyage.vesselName, no: voyage.voyageNumber, pol: voyage.pol, pod: voyage.pod,
        etd: fromVn(voyage.etd), receiveFrom: new Date(Math.min(Date.now(), cy.getTime() - 3600 * 1000)), cy, vgm: fromVn(voyage.vgmCutOff),
      },
      type: QueryTypes.SELECT,
    },
  );
  return row.id;
}

/** Đồng bộ thay đổi của chuyến xuất (tuyến, mốc giờ, trạng thái) sang export_voyages. */
async function updateInDb(voyage) {
  if (!isExportRoute(voyage)) return;
  try {
    await sequelize.query(
      `UPDATE export_voyages SET port_of_loading = :pol, port_of_discharge = :pod, etd = :etd, cy_cutoff = :cy,
              vgm_cutoff = :vgm, si_cutoff = LEAST(si_cutoff, :vgm), status = :status,
              departed_at = CASE WHEN :status = 'Departed' THEN COALESCE(departed_at, now()) ELSE departed_at END
        WHERE upper(voyage_number) = upper(:no)`,
      {
        replacements: {
          no: voyage.voyageNumber, pol: voyage.pol, pod: voyage.pod, etd: fromVn(voyage.etd), cy: fromVn(voyage.cyCutOff),
          vgm: fromVn(voyage.vgmCutOff), status: LINE_TO_DB_STATUS(voyage.status),
        },
      },
    );
  } catch (err) {
    throw new Error(`Mốc giờ chuyến ${voyage.voyageNumber} không hợp lệ (hạn hạ bãi và hạn VGM phải trước ETD): ${err.message}`);
  }
}

module.exports = { syncFromDb, createInDb, updateInDb, isExportRoute, lineKeyOfUser, lineKeyOfUsername };

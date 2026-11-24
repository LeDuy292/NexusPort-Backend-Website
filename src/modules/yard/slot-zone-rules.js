'use strict';

/**
 * Quy tắc phân khu: container LẠNH chỉ được xếp vào khu lạnh (block có is_reefer_area), container THƯỜNG chỉ được xếp
 * vào khu thường, tuyệt đối không xếp vào khu lạnh. Dùng chung cho mọi nơi ghi nhận / đề xuất vị trí bãi.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

let cache = { at: 0, codes: null };
async function reeferBlockCodes() {
  if (cache.codes && Date.now() - cache.at < 60 * 1000) return cache.codes;
  try {
    const rows = await sequelize.query('SELECT code FROM yard_blocks WHERE is_reefer_area = true', { type: QueryTypes.SELECT });
    cache = { at: Date.now(), codes: new Set(rows.map((r) => String(r.code).toUpperCase())) };
  } catch {
    cache = { at: Date.now(), codes: cache.codes || new Set() };
  }
  return cache.codes;
}

const normalizeBlock = (code) => String(code || '').trim().toUpperCase().replace(/^BLOCK\s+/, '');

async function isReeferBlock(blockCode) {
  const code = normalizeBlock(blockCode);
  if (!code) return false;
  const set = await reeferBlockCodes();
  return set.has(code) || (set.size === 0 && (code.startsWith('D') || code === 'R'));
}

/** Container có phải container lạnh không: ưu tiên dữ liệu container thật, sau đó dựa vào loại do client gửi. */
async function isReeferContainer(containerNo, typeHint) {
  const hint = String(typeHint || '').toUpperCase();
  if (/RF|RH|REEFER/.test(hint)) return true;
  const no = String(containerNo || '').trim().toUpperCase();
  if (no) {
    const [row] = await sequelize.query(
      'SELECT COALESCE(bool_or(is_reefer), false) AS reefer, count(*)::int AS n FROM containers WHERE upper(container_no) = :no',
      { replacements: { no }, type: QueryTypes.SELECT }).catch(() => []);
    if (row && row.n > 0) return Boolean(row.reefer);
  }
  return false;
}

/** Ném lỗi 422 nếu vị trí vi phạm quy tắc phân khu. */
async function assertZoneAllowed({ containerNo, blockCode, typeHint, isReefer }) {
  const reefer = typeof isReefer === 'boolean' ? isReefer : await isReeferContainer(containerNo, typeHint);
  const reeferArea = await isReeferBlock(blockCode);
  if (reefer && !reeferArea) {
    throw Object.assign(new Error(`Container lạnh ${containerNo || ''} chỉ được xếp vào khu lạnh (có ổ cắm điện), không thể xếp vào Block ${normalizeBlock(blockCode)}.`), { statusCode: 422 });
  }
  if (!reefer && reeferArea) {
    throw Object.assign(new Error(`Container thường ${containerNo || ''} không được xếp vào khu lạnh (Block ${normalizeBlock(blockCode)}). Hãy chọn khu bãi thường.`), { statusCode: 422 });
  }
}

module.exports = { isReeferBlock, isReeferContainer, assertZoneAllowed, normalizeBlock };

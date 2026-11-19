'use strict';

/**
 * Quy tắc mã chuyến tàu: {TIỀN TỐ TÀU}{YY}{STT}{CHIỀU}
 *  - Tiền tố tàu: 3 ký tự lấy từ chữ cái đầu các từ trong tên tàu (bỏ số), thiếu thì lấy tiếp chữ của từ đầu.
 *    VD: "EVER GIVEN" → EGV, "ONE CONTINUITY" → OCN, "WAN HAI 315" → WHA.
 *  - YY: 2 số cuối năm của ETA.
 *  - STT: số thứ tự 2 chữ số, tăng dần theo cùng tiền tố + năm + chiều (lấy số lớn nhất đã có + 1).
 *  - Chiều: X = xuất (rời Cảng Tiên Sa), N = nhập (đến Cảng Tiên Sa).
 *  VD: EGV2601X, EGV2602X, EGV2601N.
 * Mã booking không được suy ra từ mã chuyến.
 */

const TIEN_SA = /TIÊN SA|TIEN SA|VNDAD/i;

function vesselPrefix(vesselName) {
  const words = String(vesselName || '')
    .normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/\u0111/gi, 'D')
    .toUpperCase().split(/[^A-Z0-9]+/).filter((w) => /^[A-Z]/.test(w));
  if (words.length === 0) return 'VSL';
  let prefix = words.map((w) => w[0]).join('').slice(0, 3);
  for (let i = 1; prefix.length < 3 && i < words[0].length; i += 1) prefix += words[0][i];
  return prefix.padEnd(3, 'X');
}

function directionCode({ pol, pod }) {
  if (TIEN_SA.test(String(pol || ''))) return 'X';
  if (TIEN_SA.test(String(pod || ''))) return 'N';
  return 'X';
}

function buildVoyageNumber({ vesselName, eta, pol, pod, taken = [] }) {
  const etaDate = eta ? new Date(String(eta).includes('T') ? eta : String(eta).replace(' ', 'T')) : new Date();
  const yy = String((Number.isNaN(etaDate.getTime()) ? new Date() : etaDate).getFullYear()).slice(-2);
  const head = `${vesselPrefix(vesselName)}${yy}`;
  const dir = directionCode({ pol, pod });
  const pattern = new RegExp(`^${head}(\\d{2})${dir}$`);
  const max = taken.reduce((m, no) => {
    const hit = pattern.exec(String(no || '').toUpperCase());
    return hit ? Math.max(m, Number(hit[1])) : m;
  }, 0);
  return `${head}${String(max + 1).padStart(2, '0')}${dir}`;
}

module.exports = { vesselPrefix, directionCode, buildVoyageNumber };

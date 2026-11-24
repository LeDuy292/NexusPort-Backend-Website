'use strict';

/**
 * Xung đột vị trí đặt chỗ khi xe đã vào bãi.
 *
 * Với mỗi xe đã qua cổng vào (booking checked_in, chưa ra cổng) có container đã được giữ chỗ một ô bãi:
 *   - nếu ô đó đã có container khác / đang giữ cho container khác / đang bảo trì / bị chồng lấn bay / thiếu tầng đỡ
 *   - hệ thống TỰ ĐỘNG tạo báo cáo gửi Dispatcher (realtime) kèm tối đa 3 ô thay thế đề xuất.
 *
 * Đề xuất tuân theo quy tắc đặt container Mục 4.5.1 (cùng bộ quy tắc với thuật toán phân bổ bãi):
 *   - Cont lạnh chỉ vào khu lạnh / ô có ổ cắm điện; cont thường không chiếm ô lạnh.
 *   - Cont 20ft: bay lẻ; cont 40ft: bay chẵn (chiếm 2 mô-đun, bay lân cận phải trống).
 *   - Cont nặng (> 20 tấn): tầng 1 (tối đa tầng 2). Tầng N > 1 phải có container đỡ ở tầng N-1.
 *   - "Nặng dưới – nhẹ trên": container tầng trên không được nặng hơn tầng dưới quá 1.000 kg.
 *   - Hàng xuất: không xếp gần biển, ưu tiên khu gần cổng. Hàng nhập: ưu tiên gần cầu cảng, tránh khu gần cổng.
 *   - Cộng điểm: cùng hãng tàu trong block, cân bằng tải block (< 60%), nhóm trọng lượng đúng tầng, ổ cắm reefer,
 *     và gần vị trí đã giữ ban đầu (cùng block / cùng cụm bay) để giữ nguyên kế hoạch.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const incidents = require('./yard-incidents');
const { getIO } = require('../../config/socket');

const key = (block, bay, row, tier) => `${String(block).toUpperCase()}:${Number(bay)}:${Number(row)}:${Number(tier)}`;
const fmtPos = (block, bay, row, tier) => `${block}-${String(bay).padStart(2, '0')}-${String(row).padStart(2, '0')}-${tier}`;

function parsePos(text) {
  const parts = String(text || '').trim().toUpperCase().split(/[-.]/);
  if (parts.length < 4) return null;
  const [block, bay, row, tier] = parts;
  const b = Number(bay); const r = Number(row); const t = Number(String(tier).replace(/[^0-9]/g, ''));
  if (!block || !Number.isFinite(b) || !Number.isFinite(r) || !Number.isFinite(t)) return null;
  return { block, bay: b, row: r, tier: t };
}

const isReeferBlock = (block, slot) => Boolean(slot?.hasReeferPlug) || block.startsWith('D') || block === 'R';
const nearGate = (b) => b.startsWith('F') || b.startsWith('E') || ['B03', 'B04', 'C09', 'C10'].includes(b);
const nearSea = (b) => b.startsWith('A') || ['B01', 'B02', 'C01', 'C02', 'C03', 'C04'].includes(b);

/** Các bay mà container chiếm: 20ft = 1 bay; 40ft = bay chẵn + 2 bay lẻ lân cận. */
function bayRange(bay, lengthFt) {
  return Number(lengthFt) >= 40 ? [bay - 1, bay + 1] : [bay, bay];
}

function buildIndex(snapshot) {
  const bySlot = new Map();
  const byRowTier = new Map(); // `${block}:${row}:${tier}` -> [slot]
  const blockStats = new Map();
  for (const s of snapshot?.slots || []) {
    const k = key(s.block, s.bay, s.row, s.tier);
    bySlot.set(k, s);
    const rt = `${String(s.block).toUpperCase()}:${s.row}:${s.tier}`;
    if (!byRowTier.has(rt)) byRowTier.set(rt, []);
    byRowTier.get(rt).push(s);
    const st = blockStats.get(String(s.block).toUpperCase()) || { total: 0, used: 0 };
    st.total += 1;
    if (s.containerNumber) st.used += 1;
    blockStats.set(String(s.block).toUpperCase(), st);
  }
  return { bySlot, byRowTier, blockStats };
}

const occupied = (s) => Boolean(s && s.containerNumber && s.status !== 'empty');

/**
 * Kiểm tra một ô cho container `me`. Trả về null nếu dùng được, ngược lại { code, text }.
 */
function evaluateSlot(pos, me, idx) {
  const slot = idx.bySlot.get(key(pos.block, pos.bay, pos.row, pos.tier));
  if (!slot) return { code: 'NOT_IN_LAYOUT', text: 'ô này không tồn tại trên sơ đồ bãi' };
  if (slot.status === 'maintenance' || slot.status === 'blocked') return { code: 'MAINTENANCE', text: 'ô đang bảo trì / bị khóa' };

  const meNo = String(me.containerNo).toUpperCase();
  if (occupied(slot) && String(slot.containerNumber).toUpperCase() !== meNo) {
    return slot.status === 'reserved'
      ? { code: 'RESERVED_BY_OTHER', text: `ô đang được giữ chỗ cho container ${slot.containerNumber}` }
      : { code: 'OCCUPIED', text: `ô đã có container ${slot.containerNumber}` };
  }

  // Chồng lấn bay (cont 40ft chiếm 3 bay logic; cont 20ft nằm trong khoảng của cont 40ft)
  const [from, to] = bayRange(pos.bay, me.lengthFt);
  const sameLevel = idx.byRowTier.get(`${pos.block}:${pos.row}:${pos.tier}`) || [];
  for (const o of sameLevel) {
    if (!occupied(o) || String(o.containerNumber).toUpperCase() === meNo) continue;
    const [of, ot] = bayRange(o.bay, o.containerLengthFt || 20);
    if (!(ot < from || of > to)) {
      return { code: 'BLOCKED_OVERLAP', text: `bị chắn bởi container ${o.containerNumber} ở bay ${String(o.bay).padStart(2, '0')}` };
    }
  }

  // Tầng đỡ
  if (pos.tier > 1) {
    const below = idx.bySlot.get(key(pos.block, pos.bay, pos.row, pos.tier - 1));
    if (!occupied(below)) return { code: 'NO_SUPPORT', text: `tầng ${pos.tier - 1} bên dưới chưa có container đỡ` };
  }
  return null;
}

/** Đề xuất tối đa 3 ô thay thế theo quy tắc 4.5.1 */
function proposeSlots(me, original, idx, ctx) {
  const is20 = Number(me.lengthFt) < 40;
  const heavy = me.weightKg > 20000;
  const light = me.weightKg < 12000;
  const direction = me.direction; // 'EXPORT' | 'IMPORT'
  const out = [];

  for (const s of idx.bySlot.values()) {
    if (s.status !== 'empty' || s.mapped === false) continue;
    const block = String(s.block).toUpperCase();
    const reeferArea = isReeferBlock(block, s);
    if (me.isReefer !== reeferArea) continue; // lạnh -> khu lạnh; thường -> không vào khu lạnh
    if (is20 && s.bay % 2 === 0) continue;
    if (!is20 && s.bay % 2 !== 0) continue;
    if (heavy && s.tier >= 3) continue;
    if (direction === 'EXPORT' && nearSea(block)) continue;
    if (evaluateSlot({ block, bay: s.bay, row: s.row, tier: s.tier }, me, idx)) continue;

    const reasons = [];
    let score = 50;
    if (s.tier > 1) {
      const below = idx.bySlot.get(key(block, s.bay, s.row, s.tier - 1));
      const lowerW = ctx.weights.get(String(below?.containerNumber || '').toUpperCase());
      if (lowerW != null && me.weightKg > lowerW + 1000) continue; // nặng dưới – nhẹ trên
    }

    if (direction === 'EXPORT') {
      if (nearGate(block)) { score += 40; reasons.push(`khu tập kết hàng xuất gần cổng (Block ${block})`); }
      if (heavy && s.tier === 1) { score += 30; reasons.push('cont nặng xếp tầng 1'); }
      else if (light && s.tier >= 3) { score += 25; reasons.push('cont nhẹ xếp tầng cao'); }
      else if (!heavy && !light && (s.tier === 2 || s.tier === 3)) { score += 20; reasons.push('cont trọng lượng vừa xếp tầng 2–3'); }
    } else {
      if (nearSea(block)) { score += 45; reasons.push(`gần cầu cảng, thuận tiện bốc dỡ (Block ${block})`); }
      else if (nearGate(block)) score -= 25;
      if (heavy && s.tier === 1) { score += 25; reasons.push('cont nặng hạ tầng 1'); }
      else if (light && s.tier >= 2) { score += 15; reasons.push('cont nhẹ xếp tầng trên'); }
    }
    if (me.line && ctx.lineBlocks.get(me.line)?.has(block)) { score += 35; reasons.push(`cùng nhóm hãng tàu ${me.line}`); }
    if (me.isReefer && s.hasReeferPlug) { score += 30; reasons.push('có ổ cắm điện cho cont lạnh'); }
    const st = idx.blockStats.get(block);
    if (st && st.total > 0 && st.used / st.total < 0.6) { score += 15; reasons.push(`cân bằng tải Block ${block}`); }
    if (block === original.block) {
      if (Math.abs(s.bay - original.bay) <= 2) { score += 25; reasons.push('gần vị trí đã giữ chỗ ban đầu'); }
      else { score += 10; reasons.push('cùng block với vị trí ban đầu'); }
    }
    out.push({ slot: fmtPos(block, s.bay, s.row, s.tier), block, bay: s.bay, row: s.row, tier: s.tier, score, reasons });
  }

  out.sort((a, b) => b.score - a.score || a.slot.localeCompare(b.slot));
  // Tránh đề xuất 3 ô liền kề nhau: ưu tiên đa dạng bay / block
  const picked = [];
  for (const c of out) {
    if (picked.length >= 3) break;
    if (picked.some((p) => p.block === c.block && p.bay === c.bay && p.row === c.row)) continue;
    picked.push(c);
  }
  return picked;
}

/** Xe đã qua cổng vào, chưa ra cổng, kèm container + ô đã giữ chỗ. */
async function loadActiveArrivals() {
  const rows = await sequelize.query(`
    SELECT UPPER(c.container_no) AS "containerNo", b.booking_code AS "bookingCode", b.booking_type::text AS "bookingType",
           COALESCE(t.plate_number, b.vehicle_plate) AS "plate", COALESCE(d.full_name, b.driver_name) AS "driverName",
           c.iso_size_type AS "isoType", c.is_reefer AS "isReefer", c.gross_weight_kg AS "weightKg", sl.short_name AS "line",
           ct.size AS "sizeFt"
      FROM bookings b
      JOIN booking_containers bc ON bc.booking_id = b.id
      JOIN containers c ON c.id = bc.container_id
      LEFT JOIN container_types ct ON ct.id = c.container_type_id
      LEFT JOIN shipping_lines sl ON sl.id = c.shipping_line_id
      LEFT JOIN drivers d ON d.id = b.driver_id
      LEFT JOIN trucks t ON t.id = b.truck_id
     WHERE b.status::text = 'checked_in'
       AND EXISTS (SELECT 1 FROM gate_transactions g WHERE g.booking_id = b.id AND g.gate_type = 'gate_in' AND g.status = 'completed')
       AND NOT EXISTS (SELECT 1 FROM gate_transactions o WHERE o.booking_id = b.id AND o.gate_type = 'gate_out' AND o.status = 'completed'
                        AND o.checked_at >= (SELECT max(x.checked_at) FROM gate_transactions x WHERE x.booking_id = b.id AND x.gate_type = 'gate_in' AND x.status = 'completed'))`,
    { type: QueryTypes.SELECT });
  if (!rows.length) return [];

  const nos = rows.map((r) => r.containerNo);
  const [tasks, resv] = await Promise.all([
    sequelize.query(
      `SELECT DISTINCT ON (UPPER("ContainerNo")) UPPER("ContainerNo") AS "containerNo", "ToLocation" AS "toLocation"
         FROM yard_tasks WHERE "IsDeleted" = false AND "Status" IN ('Assigned', 'Ready', 'In_Progress') AND UPPER("ContainerNo") IN (:nos)
          AND "ToLocation" IS NOT NULL ORDER BY UPPER("ContainerNo"), "CreatedAt" DESC`,
      { replacements: { nos }, type: QueryTypes.SELECT }).catch(() => []),
    sequelize.query(
      `SELECT UPPER(container_no) AS "containerNo", block_code AS block, bay, row_no AS row, tier FROM persistent_yard_reservations WHERE UPPER(container_no) IN (:nos)`,
      { replacements: { nos }, type: QueryTypes.SELECT }).catch(() => []),
  ]);
  const taskMap = new Map(tasks.map((t) => [t.containerNo, parsePos(t.toLocation)]));
  const resvMap = new Map(resv.map((r) => [r.containerNo, { block: String(r.block).toUpperCase(), bay: Number(r.bay), row: Number(r.row), tier: Number(r.tier) }]));

  return rows.map((r) => {
    const reserved = taskMap.get(r.containerNo) || resvMap.get(r.containerNo) || null;
    const lengthFt = Number(r.sizeFt) || (String(r.isoType || '').startsWith('4') ? 40 : 20);
    return {
      containerNo: r.containerNo, bookingCode: r.bookingCode, plate: r.plate, driverName: r.driverName, line: r.line || null,
      isReefer: Boolean(r.isReefer), weightKg: Number(r.weightKg) || 0, lengthFt,
      direction: r.bookingType === 'dropoff' ? 'EXPORT' : 'IMPORT', reserved,
    };
  }).filter((r) => r.reserved);
}

let running = false;
/** Quét một lượt: tạo báo cáo xung đột mới, tự đóng báo cáo đã hết xung đột. Trả về thống kê. */
async function checkSlotConflicts() {
  if (running) return { skipped: true };
  running = true;
  try {
    const arrivals = await loadActiveArrivals();
    const open = await incidents.findOpenSlotConflicts();
    const openMap = new Map(open.map((o) => [`${String(o.containerNo).toUpperCase()}|${o.reservedSlot}`, o]));
    const stillConflicted = new Set();
    const stats = { checked: arrivals.length, created: 0, autoResolved: 0 };

    if (arrivals.length || open.length) {
      const { getLayoutSnapshotCached } = require('./yard-layout');
      const idx = buildIndex(await getLayoutSnapshotCached(8000));

      // Dữ liệu phục vụ chấm điểm (trọng lượng tầng dưới, hãng tàu theo block)
      const usedNos = [...idx.bySlot.values()].filter((s) => s.containerNumber).map((s) => String(s.containerNumber).toUpperCase());
      const info = usedNos.length ? await sequelize.query(
        `SELECT UPPER(c.container_no) AS no, c.gross_weight_kg AS w, sl.short_name AS line FROM containers c LEFT JOIN shipping_lines sl ON sl.id = c.shipping_line_id
          WHERE UPPER(c.container_no) IN (:nos)`, { replacements: { nos: usedNos }, type: QueryTypes.SELECT }).catch(() => []) : [];
      const weights = new Map(info.filter((i) => i.w != null).map((i) => [i.no, Number(i.w)]));
      const lineBlocks = new Map();
      for (const s of idx.bySlot.values()) {
        if (!s.containerNumber) continue;
        const line = info.find((i) => i.no === String(s.containerNumber).toUpperCase())?.line;
        if (!line) continue;
        if (!lineBlocks.has(line)) lineBlocks.set(line, new Set());
        lineBlocks.get(line).add(String(s.block).toUpperCase());
      }

      for (const a of arrivals) {
        const reservedText = fmtPos(a.reserved.block, a.reserved.bay, a.reserved.row, a.reserved.tier);
        const problem = evaluateSlot(a.reserved, a, idx);
        const dedup = `${a.containerNo}|${reservedText}`;
        if (!problem) continue;
        stillConflicted.add(dedup);
        if (openMap.has(dedup)) continue;

        const proposals = proposeSlots(a, a.reserved, idx, { weights, lineBlocks });
        const best = proposals[0];
        const description = `Xe ${a.plate || '—'} (tài xế ${a.driverName || '—'}) đã vào bãi với container ${a.containerNo} nhưng vị trí đã giữ chỗ ${reservedText}: ${problem.text}. `
          + (best
            ? `Hệ thống đề xuất ô ${best.slot} (${best.score} điểm): ${best.reasons.join('; ') || 'đạt chuẩn quy hoạch bãi 4.5.1'}.`
            : 'Hệ thống chưa tìm được ô thay thế đạt quy tắc đặt container 4.5.1, cần Dispatcher chỉ định thủ công.');
        await incidents.createSystemReport({
          type: 'Xung đột vị trí đặt chỗ container',
          severity: 'HIGH',
          location: `Ô ${reservedText}`,
          containerNo: a.containerNo,
          description,
          details: {
            kind: 'SLOT_CONFLICT',
            reservedSlot: reservedText,
            reasonCode: problem.code,
            reason: problem.text,
            bookingCode: a.bookingCode,
            vehiclePlate: a.plate,
            driverName: a.driverName,
            lengthFt: a.lengthFt,
            isReefer: a.isReefer,
            weightKg: a.weightKg,
            direction: a.direction,
            proposals,
          },
        });
        stats.created += 1;
      }
    }

    // Tự đóng báo cáo khi ô đã trống / đã đổi vị trí / xe đã rời bãi
    for (const o of open) {
      const dedup = `${String(o.containerNo).toUpperCase()}|${o.reservedSlot}`;
      if (stillConflicted.has(dedup)) continue;
      const closed = await incidents.resolveReport(o.id, 'Hệ thống bãi (tự động)', 'Xung đột đã hết: ô được giải phóng hoặc container đã đổi vị trí / rời bãi.');
      if (closed) stats.autoResolved += 1;
    }
    return stats;
  } finally {
    running = false;
  }
}

/** Dispatcher chấp nhận một ô đề xuất: kiểm tra lại, đổi vị trí giữ chỗ, đóng báo cáo, báo cho bãi. */
async function applyProposal(user, reportId, slotText) {
  const fail = (m, c = 422) => Object.assign(new Error(m), { statusCode: c });
  const row = await incidents.getReportRow(reportId);
  if (!row || row.details?.kind !== 'SLOT_CONFLICT') throw fail('Không tìm thấy báo cáo xung đột vị trí.', 404);
  if (row.status === 'RESOLVED') throw fail('Báo cáo này đã được xử lý.', 409);
  const target = parsePos(slotText);
  if (!target) throw fail('Vị trí đề xuất không hợp lệ.');
  const d = row.details;
  const me = { containerNo: row.container_no, lengthFt: d.lengthFt || 20, isReefer: Boolean(d.isReefer), weightKg: Number(d.weightKg) || 0, direction: d.direction };

  const { getLayoutSnapshotCached, invalidateLayoutSnapshotCache } = require('./yard-layout');
  invalidateLayoutSnapshotCache?.();
  const idx = buildIndex(await getLayoutSnapshotCached(0));
  const problem = evaluateSlot(target, me, idx);
  if (problem) throw fail(`Ô ${slotText} không còn dùng được: ${problem.text}. Hãy chọn ô khác.`, 409);

  const newText = fmtPos(target.block, target.bay, target.row, target.tier);
  await sequelize.transaction(async (transaction) => {
    await sequelize.query(
      `INSERT INTO persistent_yard_reservations (id, container_no, booking_code, block_code, bay, row_no, tier, size, status, data, updated_at)
       VALUES (:id, :c, :booking, :block, :bay, :row, :tier, :size, 'reserved', CAST(:data AS jsonb), NOW())
       ON CONFLICT (id) DO UPDATE SET block_code = EXCLUDED.block_code, bay = EXCLUDED.bay, row_no = EXCLUDED.row_no, tier = EXCLUDED.tier,
         size = EXCLUDED.size, status = 'reserved', data = EXCLUDED.data, updated_at = NOW()`,
      { replacements: { id: me.containerNo, c: me.containerNo, booking: d.bookingCode || '', block: target.block, bay: target.bay, row: target.row, tier: target.tier,
          size: me.lengthFt, data: JSON.stringify({ containerNo: me.containerNo, blockCode: target.block, bay: target.bay, row: target.row, tier: target.tier, size: me.lengthFt, source: 'dispatcher-conflict-resolution' }) },
        transaction },
    );
    await sequelize.query(
      `UPDATE yard_tasks SET "ToLocation" = :loc, "BlockCode" = :block
        WHERE UPPER("ContainerNo") = :c AND "IsDeleted" = false AND "Status" IN ('Assigned', 'Ready', 'In_Progress')`,
      { replacements: { loc: newText, block: target.block, c: me.containerNo }, transaction },
    ).catch(() => {});
  });

  // Đồng bộ ô giữ chỗ trong bản khai chuyến tàu (nếu container thuộc một bản khai)
  try {
    const manifests = await sequelize.query(
      `SELECT id, payload FROM shipping_manifests WHERE payload::text ILIKE :like`, { replacements: { like: `%${me.containerNo}%` }, type: QueryTypes.SELECT });
    for (const m of manifests) {
      let changed = false;
      const walk = (node) => {
        if (Array.isArray(node)) node.forEach(walk);
        else if (node && typeof node === 'object') {
          if (String(node.containerNumber || '').toUpperCase() === me.containerNo && 'reservedSlot' in node) {
            node.reservedSlot = newText;
            node.allocationReason = `Điều độ đổi vị trí do xung đột (${d.reservedSlot} → ${newText}) theo quy tắc 4.5.1`;
            changed = true;
          }
          Object.values(node).forEach(walk);
        }
      };
      walk(m.payload);
      if (changed) await sequelize.query('UPDATE shipping_manifests SET payload = CAST(:p AS jsonb) WHERE id = :id', { replacements: { p: JSON.stringify(m.payload), id: m.id } });
    }
  } catch (err) {
    console.warn('[SlotConflict] Không đồng bộ được bản khai:', err.message);
  }

  invalidateLayoutSnapshotCache?.();
  const byName = user?.full_name || user?.username || 'Dispatcher';
  const report = await incidents.resolveReport(reportId, byName, `Đã chuyển container ${me.containerNo} từ ${d.reservedSlot} sang ${newText}.`);
  try {
    const io = getIO();
    io?.to('yard:all').emit('yard_slot_reassigned', { containerNo: me.containerNo, from: d.reservedSlot, to: newText, by: byName });
    io?.to('gate:all').emit('yard_slot_reassigned', { containerNo: me.containerNo, from: d.reservedSlot, to: newText, by: byName });
  } catch { /* bỏ qua */ }
  return { report, containerNo: me.containerNo, from: d.reservedSlot, to: newText };
}

function startScheduler(intervalMs = 15000) {
  const timer = setInterval(() => {
    checkSlotConflicts().catch((err) => console.warn('[SlotConflict] Quét xung đột lỗi:', err.message));
  }, intervalMs);
  timer.unref?.();
  return timer;
}

module.exports = { checkSlotConflicts, applyProposal, startScheduler, evaluateSlot, proposeSlots, parsePos, buildIndex };

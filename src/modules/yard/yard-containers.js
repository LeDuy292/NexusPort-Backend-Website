'use strict';

/**
 * Danh sách container đang có mặt trong bãi (dữ liệu thật) cho trang "Chi Tiết Container".
 *  - Vị trí lấy từ bản đồ bãi (container đã đặt vào ô hoặc đang giữ chỗ).
 *  - Thông số container (loại, trọng lượng, hãng tàu, tàu/chuyến, seal) lấy từ bảng containers và các bảng liên quan.
 *  - Lịch sử dịch chuyển gom từ container_movements, lệnh bãi (yard_tasks), cổng (gate_transactions) và phiếu EIR.
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const evidenceStorage = require('../../utils/evidenceStorage');

const iso = (v) => (v ? new Date(v).toISOString() : null);
const run = (sql, replacements = {}) => sequelize.query(sql, { replacements, type: QueryTypes.SELECT });

function statusOf(slot) {
  if (slot.status === 'reserved') return 'RESERVED';
  return 'IN_YARD';
}

async function getYardContainers() {
  const { getLayoutSnapshotCached } = require('./yard-layout');
  const snapshot = await getLayoutSnapshotCached();
  const slots = (snapshot?.slots || []).filter((s) => s.containerNumber);
  const numbers = [...new Set(slots.map((s) => String(s.containerNumber).trim().toUpperCase()))];
  if (!numbers.length) return { generatedAt: new Date().toISOString(), items: [] };

  const [details, tasks, movements, gates, eirs, receipts, manifestPhotos] = await Promise.all([
    run(
      `SELECT UPPER(c.container_no) AS "containerNo", c.seal_no AS "sealNo", c.cargo_type AS "cargoType", c.gross_weight_kg AS "weightKg",
              c.is_reefer AS "isReefer", c.iso_size_type AS "isoType", c.arrived_at AS "arrivedAt", c.status::text AS status,
              ct.code AS "typeCode", sl.short_name AS "line", v.name AS "vessel", vc.call_code AS "voyage",
              (SELECT NULLIF(cv.seal_number, '') FROM container_visits cv WHERE cv.container_id = c.id AND NULLIF(cv.seal_number, '') IS NOT NULL
                ORDER BY cv.created_at DESC LIMIT 1) AS "visitSeal",
              (SELECT cv.cargo_type FROM container_visits cv WHERE cv.container_id = c.id ORDER BY cv.created_at DESC LIMIT 1) AS "visitCargo"
         FROM containers c
         LEFT JOIN container_types ct ON ct.id = c.container_type_id
         LEFT JOIN shipping_lines sl ON sl.id = c.shipping_line_id
         LEFT JOIN vessel_calls vc ON vc.id = c.vessel_call_id
         LEFT JOIN vessels v ON v.id = vc.vessel_id
        WHERE UPPER(c.container_no) IN (:numbers)`,
      { numbers },
    ).catch(() => []),
    run(
      `SELECT UPPER("ContainerNo") AS "containerNo", "TaskCode" AS code, "OperationType" AS op, "Status" AS status, "FromLocation" AS "from",
              "ToLocation" AS "to", "CreatedAt" AS "at", "StartTime" AS "startAt", "EndTime" AS "endAt", "Condition" AS condition,
              "ReceivedAt" AS "receivedAt", "ReceivedBy" AS "receivedBy"
         FROM yard_tasks WHERE "IsDeleted" = false AND UPPER("ContainerNo") IN (:numbers) ORDER BY "CreatedAt" DESC LIMIT 200`,
      { numbers },
    ).catch(() => []),
    run(
      `SELECT UPPER(container_no) AS "containerNo", movement_type AS type, from_area AS "from", to_area AS "to",
              COALESCE(moved_at, completed_at, started_at, created_at) AS at, note
         FROM container_movements WHERE UPPER(container_no) IN (:numbers) ORDER BY COALESCE(moved_at, completed_at, started_at, created_at) DESC LIMIT 200`,
      { numbers },
    ).catch(() => []),
    run(
      `SELECT UPPER(g.container_number) AS "containerNo", g.gate_type AS type, g.status, g.checked_at AS at, gt.name AS "gateName", g.actual_seal_number AS seal
         FROM gate_transactions g LEFT JOIN gates gt ON gt.id = g.gate_id
        WHERE UPPER(g.container_number) IN (:numbers) ORDER BY g.checked_at DESC LIMIT 200`,
      { numbers },
    ).catch(() => []),
    run(
      `SELECT UPPER(raw_payload->>'containerNumber') AS "containerNo", reference_no AS ref, activity, eir_status AS status, issued_at AS at,
              sound_damage_code AS "damageCode", remark, raw_payload->'damagePhotos' AS photos
         FROM eir_transactions WHERE UPPER(raw_payload->>'containerNumber') IN (:numbers) ORDER BY issued_at DESC LIMIT 100`,
      { numbers },
    ).catch(() => []),
    run(
      `SELECT UPPER(container_no) AS "containerNo", photo_urls AS photos, received_at AS at
         FROM yard_receipts WHERE COALESCE(is_deleted, false) = false AND UPPER(container_no) IN (:numbers)
          AND jsonb_array_length(COALESCE(photo_urls, '[]'::jsonb)) > 0 ORDER BY received_at DESC LIMIT 100`,
      { numbers },
    ).catch(() => []),
    run(
      `SELECT UPPER(c->>'containerNumber') AS "containerNo", c->'damagePhotos' AS photos, c->>'dischargedAt' AS at
         FROM shipping_manifests m, jsonb_array_elements(CASE WHEN jsonb_typeof(m.payload->'containers') = 'array' THEN m.payload->'containers' ELSE '[]'::jsonb END) c
        WHERE UPPER(c->>'containerNumber') IN (:numbers) AND jsonb_typeof(c->'damagePhotos') = 'array' AND jsonb_array_length(c->'damagePhotos') > 0`,
      { numbers },
    ).catch(() => []),
  ]);

  const by = (rows) => rows.reduce((m, r) => { (m.get(r.containerNo) || m.set(r.containerNo, []).get(r.containerNo)).push(r); return m; }, new Map());
  const detailMap = new Map(details.map((d) => [d.containerNo, d]));
  const taskMap = by(tasks); const moveMap = by(movements); const gateMap = by(gates); const eirMap = by(eirs);
  const receiptMap = by(receipts); const manifestPhotoMap = by(manifestPhotos);

  const rawPhotos = (arr, label, at) => (Array.isArray(arr) ? arr : [])
    .filter((v) => typeof v === 'string' && v && !v.startsWith('data:'))
    .map((value) => ({ value, label, at: iso(at) }));

  const items = await Promise.all(slots.map(async (s) => {
    const no = String(s.containerNumber).trim().toUpperCase();
    const d = detailMap.get(no) || {};
    const bay = String(s.bay).padStart(2, '0');
    const row = String(s.row).padStart(2, '0');
    const position = `${s.block}-${bay}-${row}-${s.tier}`;

    const history = [];
    (taskMap.get(no) || []).forEach((t) => history.push({
      at: iso(t.receivedAt || t.endAt || t.startAt || t.at),
      event: t.receivedAt ? 'Bãi tiếp nhận container' : `Lệnh bãi ${t.code || ''}`.trim(),
      desc: [t.op, t.from && t.to ? `${t.from} → ${t.to}` : null, t.status].filter(Boolean).join(' · '),
    }));
    (moveMap.get(no) || []).forEach((m) => history.push({
      at: iso(m.at), event: m.type || 'Dịch chuyển', desc: [m.from && m.to ? `${m.from} → ${m.to}` : null, m.note].filter(Boolean).join(' · '),
    }));
    (gateMap.get(no) || []).forEach((g) => history.push({
      at: iso(g.at), event: g.type === 'gate_out' ? 'Xe qua cổng ra' : 'Xe qua cổng vào', desc: [g.gateName, g.status].filter(Boolean).join(' · '),
    }));
    (eirMap.get(no) || []).forEach((e) => history.push({
      at: iso(e.at), event: `Phiếu e-EIR ${e.ref}`, desc: [e.activity === 'out' ? 'Xuất' : 'Nhập', e.damageCode ? `Hư hỏng: ${e.damageCode}` : null, e.remark].filter(Boolean).join(' · '),
    }));
    history.sort((a, b) => String(b.at || '').localeCompare(String(a.at || '')));

    const photoRefs = [
      ...(manifestPhotoMap.get(no) || []).flatMap((m) => rawPhotos(m.photos, 'Khi dỡ tàu', m.at)),
      ...(receiptMap.get(no) || []).flatMap((m) => rawPhotos(m.photos, 'Khi nhận vào bãi', m.at)),
      ...(eirMap.get(no) || []).flatMap((m) => rawPhotos(m.photos, `Phiếu e-EIR ${m.ref}`, m.at)),
    ];
    const seen = new Set();
    const photos = (await Promise.all(photoRefs
      .filter((p) => (seen.has(p.value) ? false : seen.add(p.value)))
      .map(async (p) => {
        try { return { url: await evidenceStorage.link(p.value), label: p.label, at: p.at }; } catch { return null; }
      }))).filter(Boolean);

    const latestEir = (eirMap.get(no) || [])[0];
    const latestTask = (taskMap.get(no) || [])[0];
    return {
      containerNo: no,
      status: statusOf(s),
      position,
      block: s.block, bay, row, tier: String(s.tier),
      type: d.typeCode || d.isoType || (s.containerLengthFt ? `${s.containerLengthFt}'` : null),
      isReefer: !!(d.isReefer || s.hasReeferPlug),
      weightKg: d.weightKg != null ? Number(d.weightKg) : null,
      sealNo: s.sealNumber || d.sealNo || d.visitSeal || null,
      cargo: d.cargoType || d.visitCargo || null,
      line: d.line || s.company || null,
      vessel: d.vessel || null,
      voyage: d.voyage || null,
      bookingCode: s.bookingCode || null,
      vehiclePlate: s.vehiclePlate || null,
      driverName: s.driverName || null,
      company: s.company || null,
      operationType: s.operationType || null,
      equipmentCode: s.equipmentCode || null,
      operatorName: s.operatorName || null,
      arrivedAt: iso(d.arrivedAt),
      condition: latestTask?.condition || (latestEir?.damageCode ? `Hư hỏng (${latestEir.damageCode})` : null),
      lastInspectionAt: iso(latestEir?.at || latestTask?.receivedAt),
      inspectionBy: latestTask?.receivedBy || null,
      photos,
      history,
    };
  }));

  items.sort((a, b) => a.position.localeCompare(b.position));
  return { generatedAt: new Date().toISOString(), items };
}

module.exports = { getYardContainers };

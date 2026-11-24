'use strict';

/**
 * Số liệu thật cho các trang Administrator: tổng quan, báo cáo, billing và quản lý cổng.
 * Mọi con số đều truy vấn trực tiếp từ database (không có dữ liệu mẫu).
 */

const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

const run = (sql, replacements = {}) => sequelize.query(sql, { replacements, type: QueryTypes.SELECT });
const safe = async (promise, fallback) => { try { return await promise; } catch (err) { console.warn('[Admin] Truy vấn lỗi:', err.message); return fallback; } };
const fail = (message, statusCode = 422) => Object.assign(new Error(message), { statusCode });
const TZ = 'Asia/Ho_Chi_Minh';

async function yardOccupancy() {
  try {
    const { getLayoutSnapshotCached } = require('../yard/yard-layout');
    const snap = await getLayoutSnapshotCached();
    const slots = snap?.slots || [];
    const used = slots.filter((s) => s.containerNumber || s.status === 'occupied' || s.status === 'reserved').length;
    return { total: slots.length, used, containers: slots.filter((s) => s.containerNumber).length };
  } catch {
    return null;
  }
}

async function getOverview() {
  const [users, carriers, bookingsByStatus, bookingsToday, gateToday, vessels, payments, paymentsMonth, unpaidOrders, shifting, incidents, trend, occupancy] = await Promise.all([
    safe(run(`SELECT role, count(*)::int AS total, count(*) FILTER (WHERE is_active)::int AS active, count(*) FILTER (WHERE session_id IS NOT NULL)::int AS online FROM users GROUP BY role ORDER BY role`), []),
    safe(run(`SELECT count(*)::int AS total, count(*) FILTER (WHERE status::text = 'active')::int AS active FROM carriers`), [{ total: 0, active: 0 }]),
    safe(run(`SELECT status::text AS status, count(*)::int AS n FROM bookings GROUP BY 1`), []),
    safe(run(`SELECT count(*)::int AS n FROM bookings WHERE (created_at AT TIME ZONE '${TZ}')::date = (now() AT TIME ZONE '${TZ}')::date`), [{ n: 0 }]),
    safe(run(`SELECT count(*) FILTER (WHERE gate_type = 'gate_in' AND status = 'completed')::int AS gate_in,
                     count(*) FILTER (WHERE gate_type = 'gate_out' AND status = 'completed')::int AS gate_out,
                     count(*) FILTER (WHERE status NOT IN ('completed', 'pending'))::int AS blocked
                FROM gate_transactions WHERE (checked_at AT TIME ZONE '${TZ}')::date = (now() AT TIME ZONE '${TZ}')::date`), [{ gate_in: 0, gate_out: 0, blocked: 0 }]),
    safe(run(`SELECT count(*)::int AS total, count(*) FILTER (WHERE status::text NOT IN ('departed', 'cancelled', 'canceled'))::int AS active FROM vessel_calls`), [{ total: 0, active: 0 }]),
    safe(run(`SELECT count(*)::int AS n, COALESCE(sum(amount), 0)::bigint AS amount FROM eport_service_payments WHERE status = 'SUCCEEDED' AND (updated_at AT TIME ZONE '${TZ}')::date = (now() AT TIME ZONE '${TZ}')::date`), [{ n: 0, amount: 0 }]),
    safe(run(`SELECT count(*)::int AS n, COALESCE(sum(amount), 0)::bigint AS amount FROM eport_service_payments WHERE status = 'SUCCEEDED' AND date_trunc('month', updated_at AT TIME ZONE '${TZ}') = date_trunc('month', now() AT TIME ZONE '${TZ}')`), [{ n: 0, amount: 0 }]),
    safe(run(`SELECT count(*)::int AS n, COALESCE(sum(r.total), 0)::bigint AS amount FROM eport_service_registrations r
               JOIN eport_service_order_states os ON os.draft_id = r.draft_id WHERE os.payment_status = 'UNPAID' AND os.status NOT IN ('CANCELLED', 'REJECTED')`), [{ n: 0, amount: 0 }]),
    safe(run(`SELECT count(*)::int AS n, COALESCE(sum(amount), 0)::bigint AS amount FROM yard_shifting_charges WHERE status = 'UNPAID'`), [{ n: 0, amount: 0 }]),
    safe(run(`SELECT severity, count(*)::int AS n FROM yard_incident_reports WHERE status <> 'RESOLVED' GROUP BY severity`), []),
    safe(run(`SELECT d::date AS day,
                     (SELECT count(*) FROM bookings b WHERE (b.created_at AT TIME ZONE '${TZ}')::date = d::date)::int AS bookings,
                     (SELECT count(*) FROM gate_transactions g WHERE g.gate_type = 'gate_in' AND g.status = 'completed' AND (g.checked_at AT TIME ZONE '${TZ}')::date = d::date)::int AS gate_in
                FROM generate_series((now() AT TIME ZONE '${TZ}')::date - 6, (now() AT TIME ZONE '${TZ}')::date, interval '1 day') d ORDER BY d`), []),
    yardOccupancy(),
  ]);

  const bookingTotal = bookingsByStatus.reduce((s, r) => s + r.n, 0);
  return {
    generatedAt: new Date().toISOString(),
    users: { byRole: users, total: users.reduce((s, r) => s + r.total, 0), online: users.reduce((s, r) => s + r.online, 0) },
    carriers: carriers[0],
    bookings: { total: bookingTotal, today: bookingsToday[0].n, byStatus: bookingsByStatus },
    gate: gateToday[0],
    vessels: vessels[0],
    yard: occupancy,
    finance: {
      paidToday: { count: payments[0].n, amount: Number(payments[0].amount) },
      paidMonth: { count: paymentsMonth[0].n, amount: Number(paymentsMonth[0].amount) },
      unpaidOrders: { count: unpaidOrders[0].n, amount: Number(unpaidOrders[0].amount) },
      unpaidShifting: { count: shifting[0].n, amount: Number(shifting[0].amount) },
    },
    incidents: { open: incidents.reduce((s, r) => s + r.n, 0), bySeverity: incidents },
    trend7d: trend.map((t) => ({ day: t.day, bookings: t.bookings, gateIn: t.gate_in })),
  };
}

async function getReports(days) {
  const span = Math.min(Math.max(Number(days) || 30, 7), 180);
  const [daily, byStatus, topCarriers, byLine, blocks, revenueDaily, gateByGate, incidentsByType] = await Promise.all([
    safe(run(`SELECT d::date AS day,
                     (SELECT count(*) FROM bookings b WHERE (b.created_at AT TIME ZONE '${TZ}')::date = d::date)::int AS bookings,
                     (SELECT count(*) FROM gate_transactions g WHERE g.gate_type = 'gate_in' AND g.status = 'completed' AND (g.checked_at AT TIME ZONE '${TZ}')::date = d::date)::int AS gate_in,
                     (SELECT count(*) FROM gate_transactions g WHERE g.gate_type = 'gate_out' AND g.status = 'completed' AND (g.checked_at AT TIME ZONE '${TZ}')::date = d::date)::int AS gate_out
                FROM generate_series((now() AT TIME ZONE '${TZ}')::date - (:span - 1), (now() AT TIME ZONE '${TZ}')::date, interval '1 day') d ORDER BY d`, { span }), []),
    safe(run(`SELECT status::text AS status, booking_type::text AS type, count(*)::int AS n FROM bookings WHERE created_at >= now() - (:span || ' days')::interval GROUP BY 1, 2 ORDER BY n DESC`, { span }), []),
    safe(run(`SELECT COALESCE(ca.company_name, 'Chưa xác định') AS name, count(*)::int AS bookings
                FROM bookings b LEFT JOIN carriers ca ON ca.id = b.carrier_id WHERE b.created_at >= now() - (:span || ' days')::interval
               GROUP BY 1 ORDER BY bookings DESC LIMIT 8`, { span }), []),
    safe(run(`SELECT COALESCE(sl.short_name, 'Chưa xác định') AS name, count(*)::int AS containers FROM containers c LEFT JOIN shipping_lines sl ON sl.id = c.shipping_line_id
               GROUP BY 1 ORDER BY containers DESC LIMIT 8`), []),
    safe(run(`SELECT yb.code AS block, count(ys.id)::int AS total, count(*) FILTER (WHERE ys.status::text IN ('occupied', 'reserved'))::int AS used
                FROM yard_blocks yb LEFT JOIN yard_slots ys ON ys.block_id = yb.id GROUP BY yb.code ORDER BY yb.code`), []),
    safe(run(`SELECT d::date AS day, COALESCE((SELECT sum(p.amount) FROM eport_service_payments p WHERE p.status = 'SUCCEEDED' AND (p.updated_at AT TIME ZONE '${TZ}')::date = d::date), 0)::bigint AS amount
                FROM generate_series((now() AT TIME ZONE '${TZ}')::date - (:span - 1), (now() AT TIME ZONE '${TZ}')::date, interval '1 day') d ORDER BY d`, { span }), []),
    safe(run(`SELECT COALESCE(gt.name, 'Chưa xác định') AS name, count(*)::int AS n FROM gate_transactions g LEFT JOIN gates gt ON gt.id = g.gate_id
               WHERE g.checked_at >= now() - (:span || ' days')::interval GROUP BY 1 ORDER BY n DESC`, { span }), []),
    safe(run(`SELECT incident_type AS name, count(*)::int AS n FROM yard_incident_reports WHERE reported_at >= now() - (:span || ' days')::interval GROUP BY 1 ORDER BY n DESC LIMIT 8`, { span }), []),
  ]);
  const sum = (arr, k) => arr.reduce((s, r) => s + Number(r[k] || 0), 0);
  return {
    days: span,
    generatedAt: new Date().toISOString(),
    totals: { bookings: sum(daily, 'bookings'), gateIn: sum(daily, 'gate_in'), gateOut: sum(daily, 'gate_out'), revenue: sum(revenueDaily, 'amount') },
    daily: daily.map((d, i) => ({ day: d.day, bookings: d.bookings, gateIn: d.gate_in, gateOut: d.gate_out, revenue: Number(revenueDaily[i]?.amount || 0) })),
    bookingStatus: byStatus,
    topCarriers,
    containersByLine: byLine,
    yardBlocks: blocks.map((b) => ({ block: b.block, total: b.total, used: b.used })),
    gateTraffic: gateByGate,
    incidentsByType,
  };
}

async function getBilling({ status } = {}) {
  const where = ['SUCCEEDED', 'PENDING', 'SUBMITTED', 'FAILED', 'CANCELLED'].includes(String(status)) ? 'WHERE p.status = :status' : '';
  const [payments, totals, shifting, shiftingTotals] = await Promise.all([
    safe(run(`SELECT p.id, p.number, p.status, p.amount, p.method, p.created_at AS "createdAt", p.updated_at AS "paidAt", d.number AS "orderNumber", ca.company_name AS "company"
                FROM eport_service_payments p LEFT JOIN eport_service_drafts d ON d.id = p.draft_id LEFT JOIN carriers ca ON ca.id = d.carrier_id
                ${where} ORDER BY p.created_at DESC LIMIT 100`, { status }), []),
    safe(run(`SELECT status, count(*)::int AS n, COALESCE(sum(amount), 0)::bigint AS amount FROM eport_service_payments GROUP BY status`), []),
    safe(run(`SELECT ch.id, ch.code, ch.container_no AS "containerNo", ch.booking_code AS "bookingCode", ch.amount, ch.status, ch.created_at AS "createdAt", ch.paid_at AS "paidAt", ca.company_name AS company, ch.reason
                FROM yard_shifting_charges ch LEFT JOIN carriers ca ON ca.id = ch.carrier_id ORDER BY (ch.status = 'PAID'), ch.created_at DESC LIMIT 100`), []),
    safe(run(`SELECT status, count(*)::int AS n, COALESCE(sum(amount), 0)::bigint AS amount FROM yard_shifting_charges GROUP BY status`), []),
  ]);
  const pick = (rows, st) => rows.find((r) => r.status === st) || { n: 0, amount: 0 };
  return {
    generatedAt: new Date().toISOString(),
    payments: payments.map((p) => ({ ...p, amount: Number(p.amount) })),
    paymentTotals: {
      succeeded: { count: pick(totals, 'SUCCEEDED').n, amount: Number(pick(totals, 'SUCCEEDED').amount) },
      pending: { count: pick(totals, 'PENDING').n + pick(totals, 'SUBMITTED').n, amount: Number(pick(totals, 'PENDING').amount) + Number(pick(totals, 'SUBMITTED').amount) },
      failed: { count: pick(totals, 'FAILED').n + pick(totals, 'CANCELLED').n, amount: Number(pick(totals, 'FAILED').amount) + Number(pick(totals, 'CANCELLED').amount) },
    },
    shiftingCharges: shifting.map((c) => ({ ...c, amount: Number(c.amount) })),
    shiftingTotals: { paid: { count: pick(shiftingTotals, 'PAID').n, amount: Number(pick(shiftingTotals, 'PAID').amount) }, unpaid: { count: pick(shiftingTotals, 'UNPAID').n, amount: Number(pick(shiftingTotals, 'UNPAID').amount) } },
    tariff: [
      { name: 'Phí đảo container 20ft (chiều xuất)', amount: 350000, unit: 'lượt đảo' },
      { name: 'Phí đảo container 40ft/45ft (chiều xuất)', amount: 550000, unit: 'lượt đảo' },
    ],
  };
}

// ── Quản lý cổng ───────────────────────────────────────────────────────────────
async function listGates() {
  const rows = await run(
    `SELECT g.id, g.code, g.name, g.location, g.is_active AS "isActive",
            (SELECT count(*) FROM gate_transactions t WHERE t.gate_id = g.id AND (t.checked_at AT TIME ZONE '${TZ}')::date = (now() AT TIME ZONE '${TZ}')::date)::int AS "todayCount",
            (SELECT max(t.checked_at) FROM gate_transactions t WHERE t.gate_id = g.id) AS "lastActivityAt"
       FROM gates g ORDER BY g.code`);
  return rows;
}
const cleanText = (v, max) => String(v == null ? '' : v).trim().slice(0, max);
async function createGate(body = {}) {
  const code = cleanText(body.code, 30).toUpperCase();
  const name = cleanText(body.name, 120);
  if (!code || !name) throw fail('Nhập mã cổng và tên cổng.');
  const dup = await run('SELECT 1 FROM gates WHERE upper(code) = :code', { code });
  if (dup.length) throw fail(`Mã cổng ${code} đã tồn tại.`, 409);
  const [row] = await run(`INSERT INTO gates (id, code, name, location, is_active) VALUES (gen_random_uuid(), :code, :name, :loc, true)
                           RETURNING id, code, name, location, is_active AS "isActive"`, { code, name, loc: cleanText(body.location, 200) || null });
  return row;
}
async function updateGate(id, body = {}) {
  if (!/^[0-9a-f-]{36}$/i.test(String(id))) throw fail('Mã cổng không hợp lệ.');
  const [row] = await run(
    `UPDATE gates SET name = COALESCE(NULLIF(:name, ''), name), location = COALESCE(:loc, location), is_active = COALESCE(:active, is_active)
      WHERE id = :id RETURNING id, code, name, location, is_active AS "isActive"`,
    { id, name: cleanText(body.name, 120), loc: body.location == null ? null : cleanText(body.location, 200), active: typeof body.isActive === 'boolean' ? body.isActive : null });
  if (!row) throw fail('Không tìm thấy cổng.', 404);
  return row;
}

// ── Quản lý thiết bị (bảng "Equipments" dùng chung với dịch vụ bãi) ─────────────
const EQUIPMENT_TYPES = ['RTG', 'ReachStacker', 'EmptyHandler', 'Forklift', 'STS', 'Tractor', 'Other'];
const EQUIPMENT_STATUSES = ['available', 'working', 'maintenance', 'inactive'];
const equipmentSelect = `"Id" AS id, "EquipmentCode" AS code, "Name" AS name, "EquipmentType" AS type, "BlockCode" AS block, "Status" AS status,
                         "OperatorName" AS "operatorName", "Description" AS description, "CreatedAt" AS "createdAt", "UpdatedAt" AS "updatedAt"`;

async function listEquipment() {
  return run(`SELECT ${equipmentSelect} FROM "Equipments" WHERE "IsDeleted" = false ORDER BY "EquipmentType", "EquipmentCode"`);
}
async function createEquipment(user, body = {}) {
  const code = cleanText(body.code, 30).toUpperCase();
  const name = cleanText(body.name, 150);
  const type = EQUIPMENT_TYPES.includes(body.type) ? body.type : 'Other';
  const status = EQUIPMENT_STATUSES.includes(body.status) ? body.status : 'available';
  if (!code || !name) throw fail('Nhập mã thiết bị và tên thiết bị.');
  const dup = await run('SELECT 1 FROM "Equipments" WHERE upper("EquipmentCode") = :code AND "IsDeleted" = false', { code });
  if (dup.length) throw fail(`Mã thiết bị ${code} đã tồn tại.`, 409);
  const [row] = await run(
    `INSERT INTO "Equipments" ("Id", "EquipmentCode", "Name", "EquipmentType", "BlockCode", "Status", "Description", "CreatedAt", "CreatedBy", "IsDeleted")
     VALUES (gen_random_uuid(), :code, :name, :type, :block, :status, :description, now(), :by, false) RETURNING ${equipmentSelect}`,
    { code, name, type, block: cleanText(body.block, 20).toUpperCase() || 'A01', status, description: cleanText(body.description, 500) || null, by: user?.id || null });
  return row;
}
async function updateEquipment(user, id, body = {}) {
  if (!/^[0-9a-f-]{36}$/i.test(String(id))) throw fail('Mã thiết bị không hợp lệ.');
  const status = EQUIPMENT_STATUSES.includes(body.status) ? body.status : null;
  const [row] = await run(
    `UPDATE "Equipments" SET "Name" = COALESCE(NULLIF(:name, ''), "Name"), "BlockCode" = COALESCE(NULLIF(:block, ''), "BlockCode"),
            "Status" = COALESCE(:status, "Status"), "Description" = COALESCE(:description, "Description"), "UpdatedAt" = now(), "UpdatedBy" = :by
      WHERE "Id" = :id AND "IsDeleted" = false RETURNING ${equipmentSelect}`,
    { id, name: cleanText(body.name, 150), block: cleanText(body.block, 20).toUpperCase(), status, description: body.description == null ? null : cleanText(body.description, 500), by: user?.id || null });
  if (!row) throw fail('Không tìm thấy thiết bị.', 404);
  return row;
}
async function deleteEquipment(user, id) {
  if (!/^[0-9a-f-]{36}$/i.test(String(id))) throw fail('Mã thiết bị không hợp lệ.');
  const [busy] = await run(`SELECT 1 FROM "Equipments" WHERE "Id" = :id AND "Status" = 'working'`, { id });
  if (busy) throw fail('Thiết bị đang làm việc, không thể xóa.', 409);
  const rows = await run(`UPDATE "Equipments" SET "IsDeleted" = true, "UpdatedAt" = now(), "UpdatedBy" = :by WHERE "Id" = :id AND "IsDeleted" = false RETURNING "Id"`, { id, by: user?.id || null });
  if (!rows.length) throw fail('Không tìm thấy thiết bị.', 404);
  return { id };
}

// ── Tạo tài khoản cho hãng tàu và gửi thông tin đăng nhập qua email ────────────────
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
async function createShippingLineAccount(body = {}) {
  const shippingLineId = String(body.shippingLineId || '');
  const email = cleanText(body.email, 200).toLowerCase();
  const password = String(body.password || '');
  const fullName = cleanText(body.fullName, 255);
  if (!/^[0-9a-f-]{36}$/i.test(shippingLineId)) throw fail('Chọn hãng tàu cần tạo tài khoản.');
  if (!EMAIL_RE.test(email)) throw fail('Email người nhận không hợp lệ.');
  if (password.length < 6) throw fail('Mật khẩu phải có ít nhất 6 ký tự.');
  const [line] = await run('SELECT id, short_name AS "shortName", full_name AS "fullName" FROM shipping_lines WHERE id = :id', { id: shippingLineId });
  if (!line) throw fail('Không tìm thấy hãng tàu.', 404);

  // Tên đăng nhập: do admin nhập, hoặc lấy phần trước @ của email
  let username = cleanText(body.username, 100) || email.split('@')[0].replace(/[^a-zA-Z0-9_.-]/g, '');
  if (username.length < 3) username = `${username}${line.shortName}`.replace(/[^a-zA-Z0-9_.-]/g, '').slice(0, 100);
  if (username.length < 3) throw fail('Tên đăng nhập phải từ 3 ký tự.');

  const usersService = require('../users/users.service');
  const created = await usersService.createUser({ username, email, password, role: 'Shipping Line', fullName: fullName || line.fullName, isActive: true });
  const userId = created.id || (created.user && created.user.id);
  await sequelize.query(
    'INSERT INTO shipping_line_accounts (user_id, shipping_line_id) SELECT :u, :l WHERE NOT EXISTS (SELECT 1 FROM shipping_line_accounts WHERE user_id = :u)',
    { replacements: { u: userId, l: shippingLineId } },
  );

  let emailSent = false;
  let emailError = null;
  if (body.sendEmail !== false) {
    try {
      await require('../../utils/mailer').sendAccountCredentialsEmail(email, {
        fullName: fullName || username, username, password, roleLabel: 'Hãng tàu', organization: line.fullName,
      });
      emailSent = true;
    } catch (err) {
      emailError = err.message;
      console.warn('[Admin] Không gửi được email tài khoản:', err.message);
    }
  }
  return { userId, username, email, shippingLine: line.shortName, emailSent, emailError };
}

module.exports = { createShippingLineAccount, getOverview, getReports, getBilling, listGates, createGate, updateGate, listEquipment, createEquipment, updateEquipment, deleteEquipment };

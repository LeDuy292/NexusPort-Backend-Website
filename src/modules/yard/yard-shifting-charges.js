'use strict';

/**
 * Phí đảo container CHIỀU XUẤT.
 *
 *  - Khi bãi đảo / di chuyển một container thuộc booking xuất (hạ container – booking_type = dropoff),
 *    hệ thống tính phí và gửi về Transport Company (chủ booking) kèm thông báo.
 *  - Transport Company thanh toán qua PayOS (VietQR). Chỉ khi TẤT CẢ phí đã thanh toán thì container mới được ra cổng
 *    (cổng và trang chuẩn bị xuất cổng đều chặn nếu còn phí chưa thanh toán).
 *
 * Biểu phí (cùng biểu phí với lệnh di chuyển container của bãi): 20ft = 350.000đ, 40ft/45ft = 550.000đ mỗi lần đảo.
 * Nguồn phát sinh phí: lệnh di chuyển (yard_tasks, OperationType = Relocate) và kế hoạch đảo (yard_restack_plans).
 * Việc điều độ tự đổi ô do xung đột vị trí (lỗi phía cảng) KHÔNG tính phí.
 */

const crypto = require('crypto');
const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');

const FEE_20 = 350000;
const FEE_40 = 550000;
const feeFor = (typeText) => (String(typeText || '').toUpperCase().includes('20') ? FEE_20 : FEE_40);

let tableReady = null;
function ensureTable() {
  if (!tableReady) {
    tableReady = sequelize.query(`
      CREATE TABLE IF NOT EXISTS yard_shifting_charges (
        id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
        code varchar(30) NOT NULL UNIQUE,
        source_key varchar(80) NOT NULL UNIQUE,
        container_no varchar(20) NOT NULL,
        booking_id uuid,
        booking_code varchar(60),
        carrier_id uuid,
        moves int NOT NULL DEFAULT 1,
        amount numeric(14,0) NOT NULL,
        reason text,
        status varchar(10) NOT NULL DEFAULT 'UNPAID',
        order_code bigint UNIQUE,
        payment_link_id varchar(80),
        checkout_url text,
        qr_code text,
        gateway_status varchar(20),
        payment_ref varchar(120),
        created_at timestamptz NOT NULL DEFAULT now(),
        paid_at timestamptz
      );
      CREATE INDEX IF NOT EXISTS idx_shifting_charge_container ON yard_shifting_charges(container_no, status);
      CREATE INDEX IF NOT EXISTS idx_shifting_charge_carrier ON yard_shifting_charges(carrier_id, status);
    `).catch((err) => { tableReady = null; throw err; });
  }
  return tableReady;
}

const fail = (message, statusCode = 422) => Object.assign(new Error(message), { statusCode });
const view = (r) => ({
  id: r.id, code: r.code, containerNo: r.container_no, bookingCode: r.booking_code, moves: Number(r.moves), amount: Number(r.amount),
  reason: r.reason, status: r.status, createdAt: r.created_at, paidAt: r.paid_at, paymentRef: r.payment_ref,
  checkoutUrl: r.status === 'UNPAID' ? r.checkout_url : null, qrCode: r.status === 'UNPAID' ? r.qr_code : null, companyName: r.company_name || null,
});

/** Container thuộc booking xuất đang hiệu lực -> { bookingId, bookingCode, carrierId } */
async function exportBookingsFor(containerNos) {
  if (!containerNos.length) return new Map();
  const rows = await sequelize.query(
    `SELECT DISTINCT ON (UPPER(c.container_no)) UPPER(c.container_no) AS no, b.id AS "bookingId", b.booking_code AS "bookingCode", b.carrier_id AS "carrierId"
       FROM bookings b JOIN booking_containers bc ON bc.booking_id = b.id JOIN containers c ON c.id = bc.container_id
      WHERE b.booking_type::text = 'dropoff' AND b.status::text NOT IN ('canceled', 'rejected', 'expired') AND UPPER(c.container_no) IN (:nos)
      ORDER BY UPPER(c.container_no), b.created_at DESC`,
    { replacements: { nos: containerNos }, type: QueryTypes.SELECT },
  );
  return new Map(rows.map((r) => [r.no, r]));
}

async function notifyCarrier(charge, carrierId) {
  if (!carrierId) return;
  try {
    const [carrier] = await sequelize.query('SELECT id, tax_code FROM carriers WHERE id = :id LIMIT 1', { replacements: { id: carrierId }, type: QueryTypes.SELECT });
    await sequelize.query(
      `INSERT INTO cargo_notifications (recipient_company_id, company_tax_code, recipient_role, event_type, direction, priority, title, message, data,
                                        action_type, action_label, action_url, document_type, document_id, dedup_key, created_at)
       VALUES (:company, :tax, 'shipper', 'YardShiftingFee', 'export', 'urgent', :title, :message, CAST(:data AS jsonb),
               'pay_fee', 'Thanh toán phí', '/shifting-fees', 'yard_shifting_charge', :docId, :dedup, now())
       ON CONFLICT DO NOTHING`,
      {
        replacements: {
          company: carrierId, tax: carrier?.tax_code || null,
          title: `Phí đảo container ${charge.container_no}: ${Number(charge.amount).toLocaleString('vi-VN')} đ`,
          message: `Bãi đã đảo container ${charge.container_no} (booking ${charge.booking_code || '—'}). Vui lòng thanh toán phí đảo chuyển; container chỉ được ra cổng sau khi thanh toán đủ.`,
          data: JSON.stringify({ chargeId: charge.id, code: charge.code, amount: Number(charge.amount) }),
          docId: charge.id, dedup: `shift:${charge.id}`,
        },
      },
    );
  } catch (err) {
    console.warn('[ShiftingCharge] Không gửi được thông báo cho công ty:', err.message);
  }
}

async function insertCharge({ sourceKey, containerNo, booking, moves, amount, reason }) {
  const code = `PD-${new Date().toISOString().slice(2, 10).replace(/-/g, '')}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`;
  const [row] = await sequelize.query(
    `INSERT INTO yard_shifting_charges (code, source_key, container_no, booking_id, booking_code, carrier_id, moves, amount, reason)
     VALUES (:code, :src, :no, :bid, :bcode, :cid, :moves, :amount, :reason)
     ON CONFLICT (source_key) DO NOTHING RETURNING *`,
    { replacements: { code, src: sourceKey, no: containerNo, bid: booking.bookingId, bcode: booking.bookingCode, cid: booking.carrierId, moves, amount, reason }, type: QueryTypes.SELECT },
  );
  if (row) await notifyCarrier(row, booking.carrierId);
  return row || null;
}

let running = false;
/** Quét lệnh di chuyển / kế hoạch đảo của container xuất và tạo phí mới (mỗi nguồn một lần). */
async function syncCharges() {
  if (running) return { skipped: true };
  running = true;
  try {
    await ensureTable();
    const [tasks, plans] = await Promise.all([
      sequelize.query(
        `SELECT "TaskCode" AS code, UPPER("ContainerNo") AS no, COALESCE("InternalFee", 0) AS fee, "ContainerType" AS type, "ShiftingReason" AS reason
           FROM yard_tasks WHERE "IsDeleted" = false AND "OperationType" = 'Relocate' AND "Status" NOT IN ('Cancelled', 'Canceled')`,
        { type: QueryTypes.SELECT }).catch(() => []),
      sequelize.query(
        `SELECT "PlanCode" AS code, UPPER("TargetContainerNo") AS no, COALESCE("EstimatedFee", 0) AS fee, "TargetContainerType" AS type, "TotalMoves" AS moves
           FROM yard_restack_plans WHERE "IsDeleted" = false AND "Status" NOT IN ('Cancelled', 'Canceled', 'Draft')`,
        { type: QueryTypes.SELECT }).catch(() => []),
    ]);
    const nos = [...new Set([...tasks, ...plans].map((x) => x.no).filter(Boolean))];
    const bookings = await exportBookingsFor(nos);
    let created = 0;
    for (const t of tasks) {
      const b = bookings.get(t.no);
      if (!b) continue; // không phải container xuất -> không tính ở luồng này
      const amount = Number(t.fee) > 0 ? Number(t.fee) : feeFor(t.type);
      if (await insertCharge({ sourceKey: `TASK:${t.code}`, containerNo: t.no, booking: b, moves: 1, amount, reason: t.reason || `Lệnh di chuyển ${t.code}` })) created += 1;
    }
    for (const p of plans) {
      const b = bookings.get(p.no);
      if (!b) continue;
      const moves = Math.max(1, Number(p.moves) || 1);
      const amount = Number(p.fee) > 0 ? Number(p.fee) : feeFor(p.type) * moves;
      if (await insertCharge({ sourceKey: `PLAN:${p.code}`, containerNo: p.no, booking: b, moves, amount, reason: `Kế hoạch đảo container ${p.code} (${moves} lượt đảo)` })) created += 1;
    }
    return { created };
  } finally {
    running = false;
  }
}

// ── Thanh toán PayOS ───────────────────────────────────────────────────────────
const payosConfig = () => ({
  clientId: process.env.PAYOS_CLIENT_ID, apiKey: process.env.PAYOS_API_KEY, checksum: process.env.PAYOS_CHECKSUM_KEY,
  returnUrl: process.env.PAYOS_RETURN_URL || 'http://localhost:5173/shifting-fees', cancelUrl: process.env.PAYOS_CANCEL_URL || 'http://localhost:5173/shifting-fees',
});
const payosReady = () => { const c = payosConfig(); return Boolean(c.clientId && c.apiKey && c.checksum); };
// Môi trường thử nghiệm dùng chung với đơn dịch vụ: số tiền chuyển thực tế cố định nhỏ (mặc định 2.000đ).
const transferAmount = (amount) => Number(process.env.SHIFTING_PAYMENT_TRANSFER_AMOUNT || 2000) || Number(amount);

async function payosCall(method, path, body) {
  const c = payosConfig();
  const res = await fetch(`https://api-merchant.payos.vn${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', 'x-client-id': c.clientId, 'x-api-key': c.apiKey },
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(15000),
  });
  const json = await res.json().catch(() => ({}));
  if (!res.ok || (json.code && json.code !== '00')) throw fail(json.desc || 'Cổng thanh toán payOS từ chối yêu cầu.', 502);
  return json.data;
}

async function loadCharge(id) {
  await ensureTable();
  if (!/^[0-9a-f-]{36}$/i.test(String(id))) throw fail('Mã phí không hợp lệ.');
  const [row] = await sequelize.query('SELECT * FROM yard_shifting_charges WHERE id = :id', { replacements: { id }, type: QueryTypes.SELECT });
  if (!row) throw fail('Không tìm thấy khoản phí.', 404);
  return row;
}

/** Công ty của người dùng (Transport Company). Administrator được xem tất cả. */
async function carrierIdOf(user) {
  if (!user || user.role === 'Administrator') return null;
  const [r] = await sequelize.query('SELECT carrier_id FROM carrier_users WHERE user_id = :u LIMIT 1', { replacements: { u: user.id }, type: QueryTypes.SELECT });
  return r?.carrier_id || undefined; // undefined = tài khoản chưa gắn công ty
}

async function assertAccess(user, row) {
  if (user?.role === 'Administrator') return;
  const cid = await carrierIdOf(user);
  if (!cid || String(cid) !== String(row.carrier_id)) throw fail('Bạn không có quyền với khoản phí này.', 403);
}

async function listCharges(user, { status, containerNo } = {}) {
  await ensureTable();
  await syncCharges().catch(() => {});
  const where = [];
  const rep = {};
  if (user?.role === 'Transport Company') {
    const cid = await carrierIdOf(user);
    if (!cid) return { items: [], summary: { unpaid: 0, unpaidAmount: 0 } };
    where.push('ch.carrier_id = :cid'); rep.cid = cid;
  }
  if (['UNPAID', 'PAID'].includes(String(status))) { where.push('ch.status = :status'); rep.status = status; }
  if (containerNo) { where.push('ch.container_no = :no'); rep.no = String(containerNo).toUpperCase(); }
  const rows = await sequelize.query(
    `SELECT ch.*, ca.company_name FROM yard_shifting_charges ch LEFT JOIN carriers ca ON ca.id = ch.carrier_id
      ${where.length ? `WHERE ${where.join(' AND ')}` : ''} ORDER BY (ch.status = 'PAID'), ch.created_at DESC LIMIT 200`,
    { replacements: rep, type: QueryTypes.SELECT });
  // Đối soát nhanh các khoản đã có link thanh toán nhưng chưa ghi nhận
  if (payosReady()) {
    await Promise.all(rows.filter((r) => r.status === 'UNPAID' && r.order_code).slice(0, 5).map((r) => refreshFromGateway(r).catch(() => {})));
    const fresh = await sequelize.query(
      `SELECT ch.*, ca.company_name FROM yard_shifting_charges ch LEFT JOIN carriers ca ON ca.id = ch.carrier_id WHERE ch.id IN (:ids)`,
      { replacements: { ids: rows.map((r) => r.id).concat(['00000000-0000-0000-0000-000000000000']) }, type: QueryTypes.SELECT });
    const map = new Map(fresh.map((r) => [r.id, r]));
    rows.forEach((r, i) => { rows[i] = map.get(r.id) || r; });
  }
  const unpaid = rows.filter((r) => r.status === 'UNPAID');
  return { items: rows.map(view), summary: { unpaid: unpaid.length, unpaidAmount: unpaid.reduce((s, r) => s + Number(r.amount), 0) } };
}

async function refreshFromGateway(row) {
  if (!row.order_code || !payosReady()) return row;
  const data = await payosCall('GET', `/v2/payment-requests/${row.order_code}`);
  const gw = String(data?.status || '').toUpperCase();
  if (gw === 'PAID') {
    await sequelize.query(
      `UPDATE yard_shifting_charges SET status = 'PAID', paid_at = COALESCE(paid_at, now()), gateway_status = 'PAID', payment_ref = COALESCE(payment_ref, :ref)
        WHERE id = :id AND status <> 'PAID'`,
      { replacements: { id: row.id, ref: data?.transactions?.[0]?.reference || String(row.order_code) } });
  } else if (gw && gw !== row.gateway_status) {
    await sequelize.query('UPDATE yard_shifting_charges SET gateway_status = :gw WHERE id = :id', { replacements: { id: row.id, gw } });
  }
  return row;
}

/** Tạo (hoặc dùng lại) link thanh toán payOS cho khoản phí. */
async function createPayment(user, id) {
  const row = await loadCharge(id);
  await assertAccess(user, row);
  if (row.status === 'PAID') throw fail('Khoản phí này đã được thanh toán.', 409);
  if (!payosReady()) throw fail('Kênh thanh toán payOS chưa được cấu hình. Liên hệ quản trị viên.', 503);

  if (row.order_code && row.checkout_url) {
    await refreshFromGateway(row);
    const again = await loadCharge(id);
    if (again.status === 'PAID') return view(again);
    if (!['CANCELLED', 'EXPIRED'].includes(String(again.gateway_status || ''))) return view(again);
  }

  const c = payosConfig();
  const orderCode = Number(`${Date.now()}`.slice(-9)) * 10 + Math.floor(Math.random() * 10);
  const amount = transferAmount(row.amount);
  const description = `PHI DAO ${String(row.container_no).slice(-7)}`.slice(0, 25);
  const signature = crypto.createHmac('sha256', c.checksum)
    .update(`amount=${amount}&cancelUrl=${c.cancelUrl}&description=${description}&orderCode=${orderCode}&returnUrl=${c.returnUrl}`).digest('hex');
  const data = await payosCall('POST', '/v2/payment-requests', {
    orderCode, amount, description, returnUrl: c.returnUrl, cancelUrl: c.cancelUrl, signature,
    items: [{ name: `Phí đảo container ${row.container_no}`, quantity: 1, price: amount }],
  });
  await sequelize.query(
    `UPDATE yard_shifting_charges SET order_code = :oc, payment_link_id = :pl, checkout_url = :url, qr_code = :qr, gateway_status = :gs WHERE id = :id`,
    { replacements: { id, oc: orderCode, pl: data?.paymentLinkId || null, url: data?.checkoutUrl || null, qr: data?.qrCode || null, gs: data?.status || 'PENDING' } });
  return view(await loadCharge(id));
}

async function syncPayment(user, id) {
  const row = await loadCharge(id);
  await assertAccess(user, row);
  await refreshFromGateway(row);
  return view(await loadCharge(id));
}

/** Số khoản phí chưa thanh toán của một container (dùng chặn ra cổng). */
async function unpaidForContainer(containerNo) {
  try {
    await ensureTable();
    const rows = await sequelize.query(
      `SELECT code, amount FROM yard_shifting_charges WHERE container_no = :no AND status = 'UNPAID'`,
      { replacements: { no: String(containerNo || '').toUpperCase() }, type: QueryTypes.SELECT });
    return { count: rows.length, amount: rows.reduce((s, r) => s + Number(r.amount), 0), codes: rows.map((r) => r.code) };
  } catch {
    return { count: 0, amount: 0, codes: [] };
  }
}

function startScheduler(intervalMs = 20000) {
  ensureTable().catch((err) => console.warn('[ShiftingCharge] Không tạo được bảng:', err.message));
  const timer = setInterval(() => {
    syncCharges().catch((err) => console.warn('[ShiftingCharge] Quét phí lỗi:', err.message));
  }, intervalMs);
  timer.unref?.();
  return timer;
}

module.exports = { ensureTable, syncCharges, listCharges, createPayment, syncPayment, unpaidForContainer, startScheduler, feeFor };

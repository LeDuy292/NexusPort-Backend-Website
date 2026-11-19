'use strict';

/**
 * Lưu chuyến tàu, manifest, lịch sử cut-off và SI của Hãng tàu trong PostgreSQL (bảng shipping_line_documents).
 *
 * Service vẫn làm việc trên các mảng như cũ. Mỗi lệnh gọi service được bọc để:
 *   1. nạp lại trạng thái từ DB (dữ liệu mẫu trong code + các dòng đã lưu đè lên),
 *   2. chạy tuần tự trong một tiến trình (không để hai lệnh gọi sửa mảng xen kẽ),
 *   3. ghi vào DB mọi tài liệu đã thay đổi khi lệnh gọi kết thúc.
 * Lệnh gọi lồng nhau (service gọi lại chính nó) chạy thẳng, không xếp hàng lại.
 */

const crypto = require('crypto');
const { AsyncLocalStorage } = require('async_hooks');
const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const data = require('./shippingLine.data');

const KINDS = {
  VESSEL: { list: data.vessels, key: (d) => d.id },
  VOYAGE: { list: data.voyages, key: (d) => d.id },
  MANIFEST: { list: data.manifests, key: (d) => d.id },
  CUTOFF: { list: data.cutOffHistory, key: (d) => crypto.createHash('sha1').update(JSON.stringify(d)).digest('hex') },
  SI: { list: data.shippingInstructions, key: (d) => d.id },
  PORT_REC: { list: data.portReconciliationRecords, key: (d) => d.id || d.containerNumber },
};

// Bản sao dữ liệu mẫu chụp trước mọi thay đổi
const SEED = Object.fromEntries(Object.entries(KINDS).map(([kind, { list }]) => [kind, JSON.parse(JSON.stringify(list))]));
const TTL_MS = 3000;
const snapshot = {}; // kind -> Map(key -> json)
let loadedAt = 0;

const store = new AsyncLocalStorage();
let tail = Promise.resolve();

async function refresh({ force = false } = {}) {
  if (!force && Date.now() - loadedAt < TTL_MS) return;
  const rows = await sequelize.query('SELECT kind, doc_key, data FROM shipping_line_documents ORDER BY seq ASC', { type: QueryTypes.SELECT });
  for (const [kind, def] of Object.entries(KINDS)) {
    const merged = JSON.parse(JSON.stringify(SEED[kind]));
    for (const row of rows.filter((r) => r.kind === kind)) {
      const idx = merged.findIndex((d) => def.key(d) === row.doc_key);
      if (idx >= 0) merged[idx] = row.data; else merged.unshift(row.data); // mới nhất ở đầu, như unshift của service
    }
    def.list.length = 0;
    def.list.push(...merged);
    snapshot[kind] = new Map(merged.map((d) => [def.key(d), JSON.stringify(d)]));
  }
  loadedAt = Date.now();
}

async function persistChanges() {
  let wrote = false;
  for (const [kind, def] of Object.entries(KINDS)) {
    const before = snapshot[kind] || new Map();
    // Ghi theo thứ tự cũ → mới để seq phản ánh thứ tự tạo (mảng có mới nhất ở đầu)
    for (const doc of [...def.list].reverse()) {
      const key = def.key(doc);
      const json = JSON.stringify(doc);
      if (before.get(key) === json) continue;
      await sequelize.query(
        `INSERT INTO shipping_line_documents(kind, doc_key, data) VALUES (:kind, :key, CAST(:data AS jsonb))
         ON CONFLICT (kind, doc_key) DO UPDATE SET data = EXCLUDED.data, updated_at = now()`,
        { replacements: { kind, key, data: json } },
      );
      before.set(key, json);
      wrote = true;
    }
    snapshot[kind] = before;
  }
  if (wrote) loadedAt = 0; // lần sau nạp lại để lấy seq chuẩn
}

/** Xóa các dòng đã lưu; gọi sau khi resetData() đã đưa mảng về dữ liệu mẫu. */
async function resetStored() {
  await sequelize.query('DELETE FROM shipping_line_documents');
  for (const [kind, def] of Object.entries(KINDS)) snapshot[kind] = new Map(def.list.map((d) => [def.key(d), JSON.stringify(d)]));
  loadedAt = 0;
}

/** Bọc mọi phương thức của service. */
function wrapService(instance, { skip = [] } = {}) {
  const proto = Object.getPrototypeOf(instance);
  for (const name of Object.getOwnPropertyNames(proto)) {
    if (name === 'constructor' || skip.includes(name) || typeof proto[name] !== 'function') continue;
    const original = proto[name];
    proto[name] = function wrapped(...args) {
      if (store.getStore()) return original.apply(this, args); // lệnh gọi lồng nhau
      const run = async () => {
        await refresh();
        try {
          return await store.run(true, () => original.apply(this, args));
        } finally {
          await persistChanges().catch((err) => console.error('[ShippingLine] Không lưu được trạng thái:', err.message));
        }
      };
      const result = tail.then(run, run);
      tail = result.catch(() => {});
      return result;
    };
  }
  return instance;
}

module.exports = { wrapService, resetStored, refresh };

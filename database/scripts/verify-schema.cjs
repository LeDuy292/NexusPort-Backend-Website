'use strict';

/**
 * Kiểm tra DB đích (ví dụ DB Railway của thành viên) có đủ cấu trúc so với database/schema-snapshot.json không.
 *
 *   $env:TARGET_DATABASE_URL = 'postgresql://...'   (PowerShell)
 *   node database/scripts/verify-schema.cjs
 *
 * Báo: bảng thiếu, cột thiếu hoặc khác kiểu, bảng thiếu khóa chính/ràng buộc duy nhất, trigger thiếu,
 * và các phiên bản migration chưa được ghi nhận. Chỉ ĐỌC, không sửa gì trong DB.
 * Thoát với mã 1 nếu còn thiếu.
 */

const fs = require('fs');
const path = require('path');
const { Client } = require('pg');

(async () => {
  const url = process.env.TARGET_DATABASE_URL || process.env.DATABASE_URL;
  if (!url) throw new Error('Đặt TARGET_DATABASE_URL (chuỗi kết nối DB cần kiểm tra).');
  const snapshotFile = path.join(__dirname, '..', 'schema-snapshot.json');
  if (!fs.existsSync(snapshotFile)) throw new Error('Chưa có database/schema-snapshot.json. Chạy export-schema-snapshot.cjs trên DB chuẩn trước.');
  const want = JSON.parse(fs.readFileSync(snapshotFile, 'utf8'));

  const local = /localhost|127\.0\.0\.1/.test(url);
  const client = new Client({ connectionString: url.replace(/\?.*/, ''), ssl: local ? false : { rejectUnauthorized: false } });
  await client.connect();
  const problems = [];
  try {
    const cols = (await client.query(`SELECT table_name, column_name, data_type, udt_name FROM information_schema.columns WHERE table_schema = 'public'`)).rows;
    const have = {};
    for (const c of cols) { have[c.table_name] ||= {}; have[c.table_name][c.column_name] = c.data_type === 'USER-DEFINED' ? c.udt_name : c.data_type; }
    const cons = (await client.query(`
      SELECT c.relname AS t, pg_get_constraintdef(k.oid) AS def FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND k.contype IN ('p', 'u')`)).rows;
    const trg = (await client.query(`
      SELECT c.relname AS t, g.tgname AS name FROM pg_trigger g JOIN pg_class c ON c.oid = g.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
       WHERE n.nspname = 'public' AND NOT g.tgisinternal`)).rows;
    let versions = [];
    try { versions = (await client.query('SELECT DISTINCT version FROM nexusport_schema_versions')).rows.map((r) => r.version); } catch { problems.push('THIẾU bảng nexusport_schema_versions (chưa chạy migration nào).'); }

    for (const [table, def] of Object.entries(want.tables)) {
      if (!have[table]) { problems.push(`THIẾU BẢNG ${table}`); continue; }
      for (const [col, meta] of Object.entries(def.columns)) {
        if (!(col in have[table])) problems.push(`THIẾU CỘT ${table}.${col} (${meta.type})`);
        else if (have[table][col] !== meta.type) problems.push(`KHÁC KIỂU ${table}.${col}: DB đích ${have[table][col]}, chuẩn ${meta.type}`);
      }
      for (const d of def.constraints) if (!cons.some((c) => c.t === table && c.def === d)) problems.push(`THIẾU RÀNG BUỘC ${table}: ${d}`);
      for (const t of def.triggers) if (!trg.some((x) => x.t === table && x.name === t)) problems.push(`THIẾU TRIGGER ${table}.${t}`);
    }
    const norm = (v) => String(v).split('_').slice(0, 2).join('_');
    const haveV = new Set(versions.map(norm));
    for (const v of want.migrations) if (!haveV.has(norm(v))) problems.push(`CHƯA GHI NHẬN migration ${v}`);
  } finally {
    await client.end();
  }

  if (problems.length === 0) { console.log('DB đích đầy đủ so với bản chuẩn.'); return; }
  console.log(`DB đích còn thiếu/khác ${problems.length} mục:`);
  for (const p of problems) console.log('  - ' + p);
  process.exit(1);
})().catch((err) => { console.error('Lỗi:', err.message); process.exit(2); });

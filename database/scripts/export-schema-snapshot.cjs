'use strict';

/**
 * Xuất "ảnh chụp cấu trúc" của DB đang chạy (bảng, cột, kiểu, khóa chính, chỉ mục duy nhất, trigger) ra
 * database/schema-snapshot.json để so sánh với DB khác bằng verify-schema.cjs.
 *
 *   node database/scripts/export-schema-snapshot.cjs        (đọc DATABASE_URL trong .env)
 *
 * Chỉ ĐỌC cấu trúc, không đọc dữ liệu, không ghi gì vào DB.
 */

const fs = require('fs');
const path = require('path');
const { Client } = require('pg');
require('dotenv').config({ path: path.join(__dirname, '..', '..', '.env') });

(async () => {
  const url = process.env.DATABASE_URL;
  if (!url) throw new Error('Thiếu DATABASE_URL');
  const client = new Client({ connectionString: url.replace(/\?.*/, ''), ssl: { rejectUnauthorized: false } });
  await client.connect();
  try {
    const cols = (await client.query(`
      SELECT table_name, column_name, data_type, udt_name, is_nullable
        FROM information_schema.columns
       WHERE table_schema = 'public' AND table_name IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public')
       ORDER BY table_name, ordinal_position`)).rows;
    const pks = (await client.query(`
      SELECT c.relname AS table_name, pg_get_constraintdef(k.oid) AS def
        FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
       WHERE n.nspname = 'public' AND k.contype IN ('p', 'u')`)).rows;
    const triggers = (await client.query(`
      SELECT c.relname AS table_name, t.tgname AS name FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND NOT t.tgisinternal`)).rows;
    const versions = (await client.query('SELECT DISTINCT version FROM nexusport_schema_versions ORDER BY 1')).rows.map((r) => r.version);

    const tables = {};
    for (const c of cols) {
      tables[c.table_name] ||= { columns: {}, constraints: [], triggers: [] };
      tables[c.table_name].columns[c.column_name] = { type: c.data_type === 'USER-DEFINED' ? c.udt_name : c.data_type, nullable: c.is_nullable === 'YES' };
    }
    for (const k of pks) if (tables[k.table_name]) tables[k.table_name].constraints.push(k.def);
    for (const t of triggers) if (tables[t.table_name]) tables[t.table_name].triggers.push(t.name);

    const file = path.join(__dirname, '..', 'schema-snapshot.json');
    fs.writeFileSync(file, JSON.stringify({ exportedAt: new Date().toISOString(), migrations: versions, tables }, null, 1));
    console.log(`Đã ghi ${Object.keys(tables).length} bảng, ${versions.length} phiên bản migration vào ${file}`);
  } finally {
    await client.end();
  }
})().catch((err) => { console.error('Lỗi:', err.message); process.exit(1); });

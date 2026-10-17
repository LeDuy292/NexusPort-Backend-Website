'use strict';
// Explicit allowlist: no directory-wide migration rollout or sample data.
const fs = require('fs');
const path = require('path');
const { createHash } = require('crypto');
const { Client } = require('../../node-core/node_modules/pg');
const files = [
  '20261017_007_yard_carrier_declarations.sql',
  '20261017_008_optional_declaration_seal.sql',
];
async function main() {
  if (!process.env.DATABASE_URL) throw new Error('DATABASE_URL must be explicitly provided.');
  const apply = process.argv.includes('--apply');
  const client = new Client({ connectionString: process.env.DATABASE_URL });
  await client.connect();
  try {
    const installed = new Set((await client.query('SELECT version FROM nexusport_schema_versions')).rows.map(row => row.version));
    for (const file of files) {
      const version = file.slice(0, 12);
      const sql = fs.readFileSync(path.join(__dirname, '../migrations', file), 'utf8');
      console.log(`${installed.has(version) ? 'Installed' : 'Missing'} ${file} sha256=${createHash('sha256').update(sql).digest('hex')}`);
      if (apply && !installed.has(version)) {
        await client.query(sql);
        console.log(`Applied ${version}`);
      }
    }
    if (apply) {
      console.log(JSON.stringify((await client.query("SELECT version FROM nexusport_schema_versions WHERE version=ANY($1::varchar[]) ORDER BY version", [files.map(f => f.slice(0, 12))])).rows));
      console.log(JSON.stringify((await client.query('SELECT code, name_vi, requires_power FROM container_cargo_types ORDER BY sort_order')).rows));
      console.log(JSON.stringify((await client.query('SELECT code, size, category, description FROM container_types ORDER BY code')).rows));
    }
  } finally { await client.end(); }
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });

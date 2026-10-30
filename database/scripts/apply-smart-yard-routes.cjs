'use strict';
const fs = require('fs');
const path = require('path');
const { Client } = require('../../node-core/node_modules/pg');

// Use the same explicit connection as Node Core; never print its credentials.
async function main() {
  if (!process.env.DATABASE_URL) throw new Error('DATABASE_URL is required. Use node --env-file=node-core/.env.');
  const client = new Client({ connectionString: process.env.DATABASE_URL, connectionTimeoutMillis: 15000 });
  await client.connect();
  try {
    const versions = new Set((await client.query('SELECT version FROM nexusport_schema_versions')).rows.map(row => row.version));
    const folder = path.join(__dirname, '../migrations');
    const target = '20261017_017_smart_yard_routes.sql';
    const pending = fs.readdirSync(folder).filter(file => file.endsWith('.sql') && file<=target && !versions.has(file.slice(0,12))).sort();
    for (const file of pending) {
      if (!process.argv.includes('--apply')) { console.log('Missing: '+file); continue; }
      await client.query(fs.readFileSync(path.join(folder,file),'utf8'));
      console.log('Applied: '+file);
    }
    if (!process.argv.includes('--apply') && pending.length) { console.log('Run with --apply to apply missing migrations in filename order.'); return; }
    const verification = await client.query("SELECT version,description FROM nexusport_schema_versions WHERE version='20261017_017'");
    if (verification.rowCount!==1) throw new Error('Smart Yard migration version was not persisted.');
    const maps = await client.query(`SELECT version,name,is_active,jsonb_array_length(map_data->'nodes') AS nodes,
      jsonb_array_length(map_data->'edges') AS edges FROM smart_yard_maps ORDER BY version`);
    console.log(JSON.stringify({ migration: verification.rows[0], maps: maps.rows }));
  } finally { await client.end(); }
}
main().catch(error => { console.error(error.message); process.exitCode=1; });

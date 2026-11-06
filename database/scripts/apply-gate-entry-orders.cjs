'use strict';
const fs = require('fs');
const path = require('path');
const { Client } = require('../../node-core/node_modules/pg');
async function main() {
  if (!process.env.DATABASE_URL) throw new Error('DATABASE_URL is required; use --env-file=node-core/.env.');
  const client = new Client({connectionString:process.env.DATABASE_URL,connectionTimeoutMillis:15000});
  await client.connect();
  try {
    const versions = new Set((await client.query('SELECT version FROM nexusport_schema_versions')).rows.map(row=>row.version));
    // The deployed reefer migration records legacy version '005', not its filename prefix.
    if(versions.has('005')) versions.add('20261012_005');
    const folder = path.join(__dirname,'../migrations');
    const pending = fs.readdirSync(folder).filter(file=>file.endsWith('.sql') && file<='20261018_020_single_entry_exit_gates.sql' && !versions.has(file.slice(0,12))).sort();
    for (const file of pending) {
      if (!process.argv.includes('--apply')) {console.log('Missing: '+file);continue;}
      await client.query(fs.readFileSync(path.join(folder,file),'utf8'));
      console.log('Applied: '+file);
    }
    if (!process.argv.includes('--apply') && pending.length) return;
    const result = await client.query("SELECT version,description FROM nexusport_schema_versions WHERE version IN ('20261018_018','20261018_019','20261018_020') ORDER BY version");
    if(result.rowCount!==3) throw new Error('Gate entry/evidence/topology migration versions were not persisted.');
    console.log(JSON.stringify(result.rows));
  } finally {await client.end();}
}
main().catch(error=>{console.error(error.message);process.exitCode=1;});

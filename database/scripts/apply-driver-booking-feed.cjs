'use strict';
const fs = require('fs');
const path = require('path');
const { sequelize } = require('../../src/config/database');
sequelize.options.logging = false;

(async () => {
  try {
    const [rows] = await sequelize.query('SELECT version FROM nexusport_schema_versions');
    const installed = new Set(rows.map(row => row.version));
    const folder = path.join(__dirname, '../migrations');
    const target = '20261017_014_driver_booking_feed.sql';
    const missing = fs.readdirSync(folder).filter(file => file.endsWith('.sql') && file < target && !installed.has(file.slice(0, 12)));
    if (missing.length) throw new Error(`Apply prerequisite migrations first: ${missing.join(', ')}`);
    if (!installed.has('20261017_014')) {
      if (!process.argv.includes('--apply')) { console.log(`Missing: ${target}. Run with --apply.`); return; }
      await sequelize.query(fs.readFileSync(path.join(folder, target), 'utf8'));
    }
    const [verified] = await sequelize.query("SELECT version,description FROM nexusport_schema_versions WHERE version='20261017_014'");
    console.log(JSON.stringify(verified));
  } finally { await sequelize.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });

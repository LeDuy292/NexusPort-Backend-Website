'use strict';
const fs = require('fs');
const path = require('path');
const { sequelize } = require('../../src/config/database');
sequelize.options.logging = false;
(async () => {
  try {
    const [rows] = await sequelize.query('SELECT version FROM nexusport_schema_versions');
    const installed = new Set(rows.map(row => row.version));
    const targets = ['20261017_015_booking_dispatch_arrivals.sql','20261017_016_dispatch_vehicle_reassignment.sql'];
    const folder = path.join(__dirname, '../migrations');
    const missing = fs.readdirSync(folder).filter(file => file.endsWith('.sql') && file < targets[0] && !installed.has(file.slice(0, 12)));
    if (missing.length) throw new Error('Apply prerequisites first: ' + missing.join(', '));
    for (const target of targets) {
      if (!installed.has(target.slice(0,12))) {
        if (!process.argv.includes('--apply')) { console.log('Missing: ' + target + '. Run with --apply.'); continue; }
        await sequelize.query(fs.readFileSync(path.join(folder, target), 'utf8'));
      }
    }
    const [verified] = await sequelize.query("SELECT version,description FROM nexusport_schema_versions WHERE version IN ('20261017_015','20261017_016') ORDER BY version");
    console.log(JSON.stringify(verified));
  } finally { await sequelize.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });

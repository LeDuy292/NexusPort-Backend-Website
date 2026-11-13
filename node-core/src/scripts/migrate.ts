import 'dotenv/config';
import { readdir, readFile } from 'fs/promises';
import { resolve } from 'path';
import pool from '../infrastructure/database/db';
import { connectWithCapacityRetry } from '../infrastructure/database/connection-capacity';

async function main() {
  let lastNotice = 0;
  const client = await connectWithCapacityRetry(() => pool.connect(), {
    timeoutMs: 30000,
    onRetry: () => {
      if (Date.now() - lastNotice >= 5000) {
        console.warn('[DB] PostgreSQL connection limit reached. Waiting for a free slot (up to 30s); schema verification is still required.');
        lastNotice = Date.now();
      }
    },
  });
  try {
    await client.query("SELECT pg_advisory_lock(hashtext('nexusport-schema-migrations'))");
    const applied = new Set((await client.query('SELECT version FROM nexusport_schema_versions')).rows.map(r => r.version));
    const folder = resolve(__dirname, '../../../database/migrations');
    const pending = (await readdir(folder)).filter(f => f.endsWith('.sql') && !applied.has(f.slice(0, 12))
      && !(f === '20261012_005_reefer_power_management.sql' && applied.has('005'))).sort();
    if (pending.length && process.argv.includes('--require-current')) throw new Error(`Database needs migrations: ${pending.join(', ')}. Run npm run db:migrate before starting Node Core.`);
    if (!pending.length) console.log('Schema is up to date.');
    for (const file of pending) {
      console.log(`Pending: ${file}`);
      if (!process.argv.includes('--apply')) continue;
      if (file === '20261110_034_seed_standard_gates.sql') {
        // The later exit-gate migration already established GATE-02. Replaying
        // this older hard-coded seed would duplicate codes and reopen GATE-03.
        const configured = await client.query("SELECT code FROM gates WHERE code IN ('GATE-01','GATE-02') AND is_active=true");
        if (applied.has('20261025_022') && configured.rowCount === 2) {
          await client.query(`INSERT INTO nexusport_schema_versions(version,description) VALUES('20261110_034',
            'Legacy standard-gate seed superseded by applied 20261025_022; retained configured GATE-01/GATE-02 IDs') ON CONFLICT(version) DO NOTHING`);
          console.log('Reconciled legacy gate seed; preserved the configured entry/exit gates.');
          continue;
        }
      }
      await client.query(await readFile(resolve(folder, file), 'utf8'));
      const version = file.slice(0, 12);
      if (!(await client.query('SELECT 1 FROM nexusport_schema_versions WHERE version=$1', [version])).rowCount)
        throw new Error(`Migration did not record its version: ${file}`);
      console.log(`Applied and verified: ${file}`);
    }
  } finally {
    await client.query('ROLLBACK').catch(() => {});
    await client.query("SELECT pg_advisory_unlock(hashtext('nexusport-schema-migrations'))").catch(() => {});
    client.release();
  }
}
main().catch(error => {
  console.error(error.code === '53300'
    ? '[DB] No PostgreSQL slot became available after 30s. Node Core was not started. Close unused DB clients or increase the database connection capacity, then retry npm run dev.'
    : error.message);
  process.exitCode = 1;
}).finally(() => pool.end());

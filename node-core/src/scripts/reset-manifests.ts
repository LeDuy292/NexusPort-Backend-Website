import 'dotenv/config';
import { mkdir, writeFile } from 'fs/promises';
import { resolve } from 'path';
import pool from '../infrastructure/database/db';

// Explicit reset tool, never a startup migration. Physical containers and port
// bookings/history are intentionally outside this allowlist.
const targets = [
  'public.shipping_manifest_bills', 'public.shipping_manifests',
];
async function main() {
  const c = await pool.connect();
  try {
    await c.query('BEGIN');
    const snapshot: Record<string, unknown> = {};
    const present: string[] = [];
    for (const table of targets) {
      if (!(await c.query('SELECT to_regclass($1) AS name', [table])).rows[0].name) continue;
      present.push(table);
      const data = (await c.query(`SELECT * FROM ${table}`)).rows;
      snapshot[table] = data;
      console.log(table, data.length);
    }
    const physicalBefore = (await c.query('SELECT count(*) AS n FROM containers')).rows[0].n;
    if (!process.argv.includes('--apply')) { await c.query('ROLLBACK'); return; }
    const folder = resolve(__dirname, '../../../../outputs/manifest-reset');
    await mkdir(folder, { recursive: true });
    await writeFile(resolve(folder, `before-${Date.now()}.json`), JSON.stringify(snapshot, null, 2), { flag: 'wx' });
    for (const table of present) console.log('Deleted', table, (await c.query(`DELETE FROM ${table}`)).rowCount);
    for (const table of present) {
      if (Number((await c.query(`SELECT count(*) AS n FROM ${table}`)).rows[0].n) !== 0) throw new Error('Reset verification failed: '+table);
    }
    if ((await c.query('SELECT count(*) AS n FROM containers')).rows[0].n !== physicalBefore) throw new Error('Physical inventory unexpectedly changed.');
    await c.query('COMMIT');
    console.log('Reset verified; physical container count preserved:', physicalBefore);
  } catch (e) { await c.query('ROLLBACK'); throw e; }
  finally { c.release(); await pool.end(); }
}
main().catch(e=>{console.error(e.message);process.exitCode=1});

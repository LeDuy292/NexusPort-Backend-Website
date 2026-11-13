import 'dotenv/config';
import { mkdir, writeFile } from 'fs/promises';
import { resolve } from 'path';
import pool from '../infrastructure/database/db';
import { connectWithCapacityRetry } from '../infrastructure/database/connection-capacity';
import { isValidIso6346 } from '../modules/container/application/container.validator';

async function main() {
  const client = await connectWithCapacityRetry(() => pool.connect(), { timeoutMs: 30000 });
  try {
    const rows = (await client.query('SELECT * FROM containers ORDER BY container_no')).rows;
    const byNumber = new Map(rows.map(row => [row.container_no, row.id]));
    const invalid = rows.filter(row => !row.identity_quarantined && !isValidIso6346(row.container_no));
    const plan = invalid.map(row => {
      const raw = String(row.container_no).trim().toUpperCase();
      const corrected = /^[A-Z]{3}[UJZ]\d{7}$/.test(raw)
        ? Array.from({ length: 10 }, (_, digit) => raw.slice(0, 10) + digit).find(isValidIso6346) : undefined;
      const collision = corrected ? Boolean(byNumber.has(corrected) && byNumber.get(corrected) !== row.id
        || invalid.some(other => other.id !== row.id && String(other.container_no).slice(0, 10) === raw.slice(0, 10))) : false;
      const action = corrected && !collision ? 'checksum_correction' : 'quarantine';
      return { id: row.id, oldNumber: row.container_no, newNumber: action === 'checksum_correction' ? corrected : null,
        collision, action,
        status: row.status, createdAt: row.created_at };
    });
    const references = (await client.query(`SELECT n.nspname AS schema,t.relname AS table,a.attname AS column
      FROM pg_constraint k JOIN pg_class t ON t.oid=k.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace
      JOIN pg_attribute a ON a.attrelid=k.conrelid AND a.attnum=k.conkey[1]
      WHERE k.contype='f' AND k.confrelid='public.containers'::regclass AND array_length(k.conkey,1)=1`)).rows;
    const quote = (name: string) => '"' + name.replace(/"/g, '""') + '"';
    const linked: Record<string, unknown> = {};
    for (const ref of references) {
      const table = `${quote(ref.schema)}.${quote(ref.table)}`, column = quote(ref.column);
      linked[`${ref.schema}.${ref.table}`] = (await client.query(`SELECT ${column} AS id,count(*)::int AS count FROM ${table}
        WHERE ${column}=ANY($1::uuid[]) GROUP BY ${column}`, [invalid.map(row => row.id)])).rows;
    }
    console.log(JSON.stringify({ total: rows.length, invalid: invalid.length, plan, references: linked }, null, 2));
    if (!process.argv.includes('--apply')) return;
    const folder = resolve(__dirname, '../../../../outputs/container-iso-repair');
    await mkdir(folder, { recursive: true });
    const reportPath = resolve(folder, `before-${Date.now()}.json`);
    await writeFile(reportPath, JSON.stringify({ plan, references: linked, rows: invalid }, null, 2), { flag: 'wx' });
    await client.query('BEGIN');
    await client.query('LOCK TABLE containers IN SHARE ROW EXCLUSIVE MODE');
    for (const change of plan) {
      const result = change.action === 'checksum_correction'
        ? await client.query('UPDATE containers SET container_no=$1,updated_at=now() WHERE id=$2 AND container_no=$3 AND NOT identity_quarantined', [change.newNumber,change.id,change.oldNumber])
        : await client.query('UPDATE containers SET identity_quarantined=true,updated_at=now() WHERE id=$1 AND container_no=$2 AND NOT identity_quarantined', [change.id,change.oldNumber]);
      if (result.rowCount !== 1) throw new Error('A planned identity changed concurrently; rolling back.');
      await client.query(`INSERT INTO container_identity_repairs(container_id,old_number,new_number,action,reason,before_snapshot)
        VALUES($1,$2,$3,$4,$5,$6)`, [change.id,change.oldNumber,change.newNumber,change.action,
        change.action === 'checksum_correction' ? 'User-requested correction of checksum only; owner code and serial preserved.' : 'Invalid category or collision with existing valid number; related history preserved.',
        invalid.find(row => row.id === change.id)]);
    }
    const after = (await client.query('SELECT id,container_no,identity_quarantined FROM containers')).rows;
    if (after.length !== rows.length || after.some(row => !row.identity_quarantined && !isValidIso6346(row.container_no))) throw new Error('Post-repair verification failed.');
    await client.query('ALTER TABLE containers VALIDATE CONSTRAINT ck_containers_active_iso6346');
    await client.query('COMMIT');
    console.log('Corrected:',plan.filter(row=>row.action==='checksum_correction').length,'Quarantined:',plan.filter(row=>row.action==='quarantine').length,'Remaining active invalid:',0,'Container IDs and FK links preserved.');
    console.log('Backup:', reportPath);
  } catch (error) { await client.query('ROLLBACK').catch(()=>{}); throw error; }
  finally { client.release(); }
}
main().catch(e=>{console.error(e.message);process.exitCode=1}).finally(()=>pool.end());

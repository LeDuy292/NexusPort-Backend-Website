import 'dotenv/config';
import { Client } from 'pg';
import pool from '../infrastructure/database/db';
async function main() {
  for (let attempt = 1; attempt <= 10; attempt++) {
    const client = new Client({ ...pool.options, application_name: 'nexusport-db-capacity-check', connectionTimeoutMillis: 5000 });
    try {
      await client.connect();
      console.log('CAPACITY', (await client.query(`SELECT current_setting('max_connections') AS max_connections,
        current_setting('superuser_reserved_connections') AS superuser_reserved_connections,
        (SELECT count(*) FROM pg_stat_activity) AS visible_backends`)).rows);
      console.log('CONNECTIONS', (await client.query(`SELECT usename,application_name,state,count(*)::int AS connections
        FROM pg_stat_activity GROUP BY usename,application_name,state ORDER BY connections DESC`)).rows);
      console.log('ROLE_LIMIT', (await client.query('SELECT rolconnlimit FROM pg_roles WHERE rolname=current_user')).rows);
      return;
    } catch (error) {
      if ((error as {code?:string}).code !== '53300' || attempt === 10) throw error;
      console.log(`Database capacity unavailable; inspection retry ${attempt}/10.`);
    } finally { await client.end().catch(() => {}); }
    await new Promise(resolve => setTimeout(resolve, 2000));
  }
}
main().catch(e=>{ console.error(e.message); process.exitCode=1; }).finally(()=>pool.end());

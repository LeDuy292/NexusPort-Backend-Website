import 'dotenv/config';
import pool from '../infrastructure/database/db';
import { writeFile } from 'fs/promises';
import { connectWithCapacityRetry } from '../infrastructure/database/connection-capacity';
async function main() {
 const client=await connectWithCapacityRetry(()=>pool.connect(),{timeoutMs:30000});
 try {
  const report = {
   drivers: (await client.query(`SELECT d.*,u.username,u.email AS account_email,u.is_active,
    (SELECT count(*)::int FROM bookings b WHERE b.driver_id=d.id) AS bookings FROM drivers d LEFT JOIN users u ON u.id=d.user_id ORDER BY d.full_name`)).rows,
   users: (await client.query(`SELECT id,username,email,full_name,role,is_active FROM users WHERE role IN ('Driver','Berth Staff') OR username ILIKE '%tien%' OR username ILIKE '%dauboquay%'`)).rows,
   references: (await client.query(`SELECT tc.table_name,kcu.column_name,ccu.table_name AS foreign_table FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.constraint_name=kcu.constraint_name AND tc.constraint_schema=kcu.constraint_schema JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name=tc.constraint_name AND ccu.constraint_schema=tc.constraint_schema WHERE tc.constraint_type='FOREIGN KEY' AND ccu.table_name IN ('drivers','users')`)).rows
  };
  if(process.env.DRIVER_AUDIT_REPORT) await writeFile(process.env.DRIVER_AUDIT_REPORT,JSON.stringify(report,null,2));
  console.log(JSON.stringify({drivers:report.drivers.map(d=>({id:d.id,name:d.full_name,username:d.username,bookings:d.bookings,linked:!!d.username})),users:report.users,referenceCount:report.references.length},null,2));
 }finally{client.release();}
}
main().catch(e=>{console.error(e.message);process.exitCode=1;}).finally(()=>pool.end());

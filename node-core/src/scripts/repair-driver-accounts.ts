import 'dotenv/config';
import { randomBytes } from 'crypto';
import { writeFile } from 'fs/promises';
import bcrypt from 'bcryptjs';
import pool from '../infrastructure/database/db';

// Explicit operator-approved maintenance, never invoked by startup or migrations.
// Preserve every booked driver, existing credentials, and named accounts.
async function main() {
 const client=await pool.connect();
 try {
  await client.query('BEGIN');
  await client.query('LOCK TABLE drivers,users,bookings IN SHARE ROW EXCLUSIVE MODE');
  const drivers=(await client.query('SELECT * FROM drivers')).rows;
  const users=(await client.query('SELECT * FROM users')).rows;
  const memberships=(await client.query('SELECT * FROM carrier_users')).rows;
  // User explicitly allowed any empty profile for dauboquay9 (2026-11-14).
  const account9=users.find(u=>u.username==='dauboquay9' && u.role==='Driver');
  const emptyProfile=drivers.find(d=>!d.user_id);
  if(account9 && !drivers.some(d=>d.user_id===account9.id) && emptyProfile) emptyProfile.user_id=account9.id;
  const booked=new Set((await client.query('SELECT DISTINCT driver_id FROM bookings WHERE driver_id IS NOT NULL')).rows.map(r=>r.driver_id));
  const keepNames=new Set(['tiendriver','dauboquay','dauboquay9']);
  const userMap=new Map(users.map(u=>[u.id,u]));
  const keepUsers=new Set(users.filter(u=>keepNames.has(u.username.toLowerCase())).map(u=>u.id));
  for(const d of drivers) if(booked.has(d.id) && d.user_id) keepUsers.add(d.user_id);
  const removeDrivers=drivers.filter(d=>!booked.has(d.id) && !keepUsers.has(d.user_id));
  const removeUsers=users.filter(u=>(u.role==='Driver' && !keepUsers.has(u.id)) || u.role==='Berth Staff');
  const recover=drivers.filter(d=>booked.has(d.id) && d.user_id && !userMap.has(d.user_id));
  console.log(JSON.stringify({deleteDrivers:removeDrivers.map(d=>({id:d.id,name:d.full_name})),deleteUsers:removeUsers.map(u=>({id:u.id,username:u.username})),recoverUsers:recover.map(d=>({id:d.user_id,driverId:d.id,name:d.full_name})),bookedDrivers:booked.size},null,2));
  if(!process.argv.includes('--apply')) {await client.query('ROLLBACK');return;}
  const backupPath=process.env.DRIVER_REPAIR_BACKUP;
  if(!backupPath) throw new Error('DRIVER_REPAIR_BACKUP is required.');
  const admin=users.find(u=>u.role==='Administrator' && u.is_active);
  if(!admin) throw new Error('No active administrator.');
  // Full pre-change records, including password hashes, remain outside the repo.
  await writeFile(backupPath,JSON.stringify({createdAt:new Date().toISOString(),drivers:(await client.query('SELECT * FROM drivers')).rows,users,memberships,
   truckAssignments:(await client.query('SELECT id,driver_id FROM trucks WHERE driver_id IS NOT NULL')).rows},null,2),{flag:'wx'});
  const bookingSnapshot=(await client.query('SELECT id,driver_id,carrier_id FROM bookings ORDER BY id')).rows;
  if(account9 && emptyProfile) await client.query('UPDATE drivers SET user_id=$2 WHERE id=$1 AND user_id IS NULL',[emptyProfile.id,account9.id]);
  for(const d of recover) {
   if(!d.email || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(d.email)) throw new Error(`Missing verified contact for driver ${d.id}`);
   // Historical backup driver-profile-012345678901-before-removal.json identifies
   // this precise account ID as tiendriver. Other usernames are stable new names.
   const username=d.user_id==='e738b67f-dba6-4f5e-895f-eb2e81afe3bc'?'tiendriver':`driver_${d.id.slice(0,8)}`;
   await client.query(`INSERT INTO users(id,username,email,password,role,full_name,is_active,invitation_pending,created_at,updated_at)
    VALUES($1,$2,$3,$4,'Driver',$5,false,true,now(),now())`,[d.user_id,username,d.email.toLowerCase(),await bcrypt.hash(randomBytes(32).toString('hex'),12),d.full_name]);
   const profile={username,email:d.email.toLowerCase(),driverId:d.id,fullName:d.full_name,phone:d.phone,idCardNumber:d.id_card_number,licenseNumber:d.license_number};
   await client.query(`INSERT INTO account_requests(role,carrier_id,driver_id,requested_by,username,email,profile,status,reviewed_by,reviewed_at,user_id)
    VALUES('Driver',$1,$2,$3,$4,$5,$6,'approved',$3,now(),$7)`,[d.carrier_id,d.id,admin.id,username,d.email.toLowerCase(),JSON.stringify(profile),d.user_id]);
  }
  for(const d of removeDrivers) {
   // Trucks may retain a test driver assignment; clear only non-booked candidates.
   await client.query('UPDATE trucks SET driver_id=NULL WHERE driver_id=$1',[d.id]);
   await client.query('DELETE FROM drivers WHERE id=$1 AND NOT EXISTS(SELECT 1 FROM bookings WHERE driver_id=$1)',[d.id]);
  }
  for(const u of removeUsers) {
   await client.query('DELETE FROM carrier_users WHERE user_id=$1',[u.id]);
   await client.query('DELETE FROM users WHERE id=$1',[u.id]);
  }
  await client.query(`INSERT INTO carrier_users(carrier_id,user_id) SELECT d.carrier_id,d.user_id FROM drivers d JOIN users u ON u.id=d.user_id
   WHERE u.role='Driver' AND NOT EXISTS(SELECT 1 FROM carrier_users cu WHERE cu.carrier_id=d.carrier_id AND cu.user_id=d.user_id)`);
  const after=(await client.query('SELECT id,driver_id,carrier_id FROM bookings ORDER BY id')).rows;
  if(JSON.stringify(bookingSnapshot)!==JSON.stringify(after)) throw new Error('Booking references changed; aborting.');
  await client.query('COMMIT');
  console.log(`Repaired ${recover.length} missing accounts; deleted ${removeDrivers.length} unbooked profiles and ${removeUsers.length} test/retired accounts. All ${after.length} bookings preserved.`);
 }catch(e){await client.query('ROLLBACK');throw e;}finally{client.release();}
}
main().catch(e=>{console.error(e.message);process.exitCode=1;}).finally(()=>pool.end());

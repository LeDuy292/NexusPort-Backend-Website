import 'dotenv/config';
import assert from 'assert/strict';
import { randomUUID } from 'crypto';
import { createServer, Server } from 'http';
import jwt from 'jsonwebtoken';
import pool from '../infrastructure/database/db';
import { createApp } from '../api/app';

// Real HTTP + SQL, with a local email receiver. Never sends email to real people.
const target = new URL(process.env.DATABASE_URL || 'postgresql://invalid');
if (target.hostname !== '127.0.0.1' || target.port !== '55438' || target.pathname !== '/nxp038_test')
 throw new Error('This check only runs on isolated 127.0.0.1:55438/nxp038_test.');
const listen = (server: Server) => new Promise<number>(resolve => server.listen(0,'127.0.0.1',()=>resolve((server.address() as {port:number}).port)));
const close = (server: Server) => new Promise<void>(resolve=>server.close(()=>resolve()));
async function main() {
 const admin=randomUUID(), company=randomUUID(), other=randomUUID(), carrier=randomUUID(), otherCarrier=randomUUID();
 const suffix=randomUUID().slice(0,8);
 const mail: {activationUrl:string;to:string}[]=[];
 let failEmail=false;
 const emailServer=createServer(async(req,res)=>{
  let body='';for await(const chunk of req)body+=chunk;
  assert.equal(req.headers['x-account-email-key'],'local-test-only');
  if(!failEmail)mail.push(JSON.parse(body));
  res.writeHead(failEmail?503:200);res.end('{}');
 });
 const emailPort=await listen(emailServer);
 process.env.ACCOUNT_EMAIL_SERVICE_URL=`http://127.0.0.1:${emailPort}`;
 process.env.ACCOUNT_EMAIL_SERVICE_KEY='local-test-only';process.env.ACCOUNT_ACTIVATION_ORIGIN='http://localhost:5173';
 const server=createServer(createApp()); const port=await listen(server);
 const token=(id:string)=>jwt.sign({sub:id,id},process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!',{expiresIn:'5m'});
 const request=async(method:string,path:string,body?:unknown,actor?:string)=>{
  const response=await fetch(`http://127.0.0.1:${port}/api/v1${path}`,{method,headers:{'Content-Type':'application/json',...(actor?{Authorization:`Bearer ${token(actor)}`}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});
  return {status:response.status,body:await response.json() as any};
 };
 const profile={username:`driver_${suffix}`,email:`${suffix}@example.invalid`,fullName:'Integration Driver',phone:'0912345678',idCardNumber:'012345678912',licenseNumber:`test-${suffix}`};
 try {
  for(const [id,role] of [[admin,'Administrator'],[company,'Transport Company'],[other,'Transport Company']]) await pool.query(`INSERT INTO users(id,username,email,password,role,is_active,created_at,updated_at) VALUES($1,$1::uuid::text,$2,'test-only',$3,true,now(),now())`,[id,`${id}@example.invalid`,role]);
  await pool.query("INSERT INTO carriers(id,company_name) VALUES($1,'Integration Company A'),($2,'Integration Company B')",[carrier,otherCarrier]);
  await pool.query('INSERT INTO carrier_users(carrier_id,user_id) VALUES($1,$2),($3,$4)',[carrier,company,otherCarrier,other]);
  assert.equal((await request('POST','/account-requests',profile)).status,401);
  assert.equal((await request('POST','/account-requests',{...profile,carrierId:otherCarrier},company)).status,422);
  const submitted=await request('POST','/account-requests',profile,company);assert.equal(submitted.status,201);const id=submitted.body.data.id;
  assert.equal((await pool.query('SELECT 1 FROM users WHERE username=$1',[profile.username])).rowCount,0);
  assert.equal((await request('GET','/account-requests',undefined,other)).body.data.length,0);
  assert.equal((await request('POST',`/account-requests/${id}/approve`,{},company)).status,403);
  assert.equal((await request('POST','/account-requests',profile,company)).status,409);
  failEmail=true;
  assert.equal((await request('POST',`/account-requests/${id}/approve`,{},admin)).status,422);
  assert.equal((await pool.query('SELECT status FROM account_requests WHERE id=$1',[id])).rows[0].status,'pending');
  assert.equal((await pool.query('SELECT 1 FROM users WHERE username=$1',[profile.username])).rowCount,0);
  failEmail=false;
  const approvals=await Promise.all([request('POST',`/account-requests/${id}/approve`,{},admin),request('POST',`/account-requests/${id}/approve`,{},admin)]);
  assert.deepEqual(approvals.map(r=>r.status).sort(),[200,409]);
  const user=(await pool.query('SELECT * FROM users WHERE username=$1',[profile.username])).rows[0];
  assert.equal(user.is_active,false);assert.equal(user.invitation_pending,true);
  assert.equal((await pool.query('SELECT status FROM drivers WHERE user_id=$1',[user.id])).rows[0].status,'inactive');
  assert.equal((await pool.query('SELECT carrier_id FROM drivers WHERE user_id=$1',[user.id])).rows[0].carrier_id,carrier);
  assert.equal((await pool.query('SELECT carrier_id FROM carrier_users WHERE user_id=$1',[user.id])).rows[0].carrier_id,carrier);
  const getToken=(n:number)=>new URLSearchParams(new URL(mail[n].activationUrl).hash.slice(1)).get('token');
  const oldToken=getToken(0);
  assert.equal((await request('POST',`/account-requests/${id}/resend`,{},admin)).status,200);
  assert.equal((await request('POST','/auth/activate-account',{token:oldToken,password:'DriverSecure123!'})).status,422);
  const expiredToken=getToken(1);
  await pool.query("UPDATE account_invitations SET expires_at=now()-interval '1 second' WHERE user_id=$1",[user.id]);
  assert.equal((await request('POST','/auth/activate-account',{token:expiredToken,password:'DriverSecure123!'})).status,422);
  assert.equal((await request('POST',`/account-requests/${id}/resend`,{},admin)).status,200);
  const validToken=getToken(2);
  const stored=(await pool.query('SELECT token_hash,expires_at,created_at FROM account_invitations WHERE user_id=$1 AND used_at IS NULL',[user.id])).rows[0];
  assert.notEqual(stored.token_hash,validToken);assert.equal(Number(stored.expires_at)-Number(stored.created_at),86400000);
  assert.equal((await request('PATCH',`/users/${user.id}/activate`,{},company)).status,403);
  await assert.rejects(pool.query('UPDATE users SET is_active=true WHERE id=$1',[user.id]));
  assert.equal((await request('POST','/auth/activate-account',{token:validToken,password:'short'})).status,422);
  assert.equal((await request('POST','/auth/activate-account',{token:validToken,password:'DriverSecure123!'})).status,200);
  assert.equal((await pool.query('SELECT status FROM drivers WHERE user_id=$1',[user.id])).rows[0].status,'active');
  assert.equal((await request('POST','/auth/activate-account',{token:validToken,password:'AnotherPass123!'})).status,422);
  const login=await request('POST','/auth/login',{username:profile.username,password:'DriverSecure123!'});assert.equal(login.status,200);
  assert.ok(login.body.data.token);assert.equal(login.body.data.user.role,'Driver');
  assert.equal((await request('POST',`/account-requests/${id}/resend`,{},admin)).status,409);
  const driver=(await pool.query('SELECT id FROM drivers WHERE user_id=$1',[user.id])).rows[0];
  assert.equal((await request('POST','/account-requests',{...profile,username:`other_${suffix}`,email:`other_${suffix}@example.invalid`,driverId:driver.id},other)).status,409);
  console.log('PASS: HTTP/SQL tenancy, approval race, email rollback, 24-hour expiry, resend revocation, one-time activation, password login and activation bypass guards.');
 } finally {
  const ids=(await pool.query('SELECT id FROM users WHERE username=$1',[profile.username])).rows.map(r=>r.id);
  await pool.query('DELETE FROM account_invitations WHERE user_id=ANY($1::uuid[])',[ids]);
  await pool.query('DELETE FROM account_requests WHERE requested_by=ANY($1::uuid[])',[[company,other]]);
  await pool.query('DELETE FROM drivers WHERE carrier_id=ANY($1::uuid[])',[[carrier,otherCarrier]]);
  await pool.query('DELETE FROM carrier_users WHERE carrier_id=ANY($1::uuid[])',[[carrier,otherCarrier]]);
  await pool.query('DELETE FROM carriers WHERE id=ANY($1::uuid[])',[[carrier,otherCarrier]]);
  await pool.query('DELETE FROM users WHERE id=ANY($1::uuid[])',[[admin,company,other,...ids]]);
  await close(server);await close(emailServer);
 }
}
main().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>pool.end());

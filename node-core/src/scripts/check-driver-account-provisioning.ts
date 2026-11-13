import 'dotenv/config';
import assert from 'assert/strict';
import { randomUUID } from 'crypto';
import { createServer, Server } from 'http';
import jwt from 'jsonwebtoken';
import pool from '../infrastructure/database/db';
import { createApp } from '../api/app';

const target = new URL(process.env.DATABASE_URL || 'postgresql://invalid');
if (target.hostname !== '127.0.0.1' || target.port !== '55438' || target.pathname !== '/nxp038_test')
  throw new Error('This check only runs on isolated 127.0.0.1:55438/nxp038_test.');
const listen = (server: Server) => new Promise<number>(resolve => server.listen(0, '127.0.0.1', () => resolve((server.address() as {port:number}).port)));
const close = (server: Server) => new Promise<void>(resolve => server.close(() => resolve()));

async function main() {
  const admin = randomUUID(), company = randomUUID(), other = randomUUID(), carrier = randomUUID(), otherCarrier = randomUUID();
  const legacyUser = randomUUID(), legacyDriver = randomUUID();
  const suffix = randomUUID().slice(0, 8);
  const mail: {activationUrl: string; to: string}[] = [];
  let failEmail = false;
  const emailServer = createServer(async (req, res) => {
    let body = ''; for await (const chunk of req) body += chunk;
    assert.equal(req.headers['x-account-email-key'], 'local-test-only');
    if (!failEmail) mail.push(JSON.parse(body));
    res.writeHead(failEmail ? 503 : 200); res.end('{}');
  });
  process.env.ACCOUNT_EMAIL_SERVICE_URL = `http://127.0.0.1:${await listen(emailServer)}`;
  process.env.ACCOUNT_EMAIL_SERVICE_KEY = 'local-test-only';
  process.env.ACCOUNT_ACTIVATION_ORIGIN = 'http://localhost:5173';
  const server = createServer(createApp()); const port = await listen(server);
  const token = (id: string) => jwt.sign({sub: id, id}, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!', {expiresIn: '5m'});
  const request = async (method: string, path: string, body?: unknown, actor?: string) => {
    const response = await fetch(`http://127.0.0.1:${port}/api/v1${path}`, {method,
      headers: {'Content-Type': 'application/json', ...(actor ? {Authorization: `Bearer ${token(actor)}`} : {})},
      ...(body === undefined ? {} : {body: JSON.stringify(body)})});
    return {status: response.status, body: await response.json().catch(() => ({})) as any};
  };
  const profile = {username: `driver_${suffix}`, email: `${suffix}@example.invalid`, fullName: 'Integration Driver',
    phone: '0912345678', idCardNumber: '012345678912', licenseNumber: `test-${suffix}`};
  const lastToken = () => new URLSearchParams(new URL(mail[mail.length - 1].activationUrl).hash.slice(1)).get('token');
  const activation = (value: string | null, password = 'DriverSecure123!') => request('POST', '/auth/activate-account', {token: value, password});
  try {
    for (const [id, role] of [[admin, 'Administrator'], [company, 'Transport Company'], [other, 'Transport Company'], [legacyUser, 'Driver']])
      await pool.query(`INSERT INTO users(id,username,email,password,role,is_active,created_at,updated_at)
        VALUES($1,$1::uuid::text,$2,'test-only',$3,true,now(),now())`, [id, `${id}@example.invalid`, role]);
    await pool.query("INSERT INTO carriers(id,company_name) VALUES($1,'Integration Company A'),($2,'Integration Company B')", [carrier, otherCarrier]);
    await pool.query('INSERT INTO carrier_users(carrier_id,user_id) VALUES($1,$2),($3,$4)', [carrier, company, otherCarrier, other]);
    await pool.query(`INSERT INTO drivers(id,carrier_id,user_id,full_name,license_number,status) VALUES($1,$2,$3,'Legacy driver',$4,'active')`, [legacyDriver, carrier, legacyUser, `legacy-${suffix}`]);
    assert.equal((await request('POST', '/driver-accounts', profile)).status, 401);
    assert.equal((await request('POST', '/driver-accounts', profile, admin)).status, 403);
    assert.equal((await request('POST', '/driver-accounts', {...profile, carrierId: otherCarrier}, company)).status, 422);
    assert.equal((await request('POST', '/account-requests', profile, company)).status, 404);
    failEmail = true;
    assert.equal((await request('POST', '/driver-accounts', profile, company)).status, 422);
    assert.equal((await pool.query('SELECT 1 FROM users WHERE username=$1', [profile.username])).rowCount, 0);
    assert.equal((await pool.query('SELECT 1 FROM account_requests WHERE username=$1', [profile.username])).rowCount, 0);
    assert.equal((await pool.query('SELECT 1 FROM drivers WHERE license_number=$1', [profile.licenseNumber])).rowCount, 0);
    failEmail = false;
    const creation = await Promise.all([request('POST', '/driver-accounts', profile, company), request('POST', '/driver-accounts', profile, company)]);
    assert.deepEqual(creation.map(r => r.status).sort(), [201, 409]);
    const created = creation.find(r => r.status === 201)!.body.data;
    const user = (await pool.query('SELECT * FROM users WHERE id=$1', [created.userId])).rows[0];
    assert.equal(user.is_active, false); assert.equal(user.invitation_pending, true); assert.equal(user.email_verified_at, null);
    assert.equal(mail.length, 1); assert.equal(mail[0].to, profile.email);
    const audit = (await pool.query('SELECT status,reviewed_by FROM account_requests WHERE user_id=$1', [user.id])).rows[0];
    assert.equal(audit.status, 'approved'); assert.equal(audit.reviewed_by, null);
    assert.equal((await pool.query('SELECT status,carrier_id FROM drivers WHERE id=$1', [created.driverId])).rows[0].status, 'inactive');
    assert.equal((await pool.query('SELECT carrier_id FROM carrier_users WHERE user_id=$1', [user.id])).rows[0].carrier_id, carrier);
    assert.equal((await request('POST', '/auth/login', {username: profile.username, password: 'DriverSecure123!'})).status, 403);
    assert.equal((await request('GET', '/driver-accounts/driver-links', undefined, other)).body.data.length, 0);
    assert.equal((await request('POST', `/driver-accounts/${created.driverId}/resend`, {}, other)).status, 404);
    let links = (await request('GET', '/driver-accounts/driver-links', undefined, company)).body.data;
    assert.equal(links.find((d: any) => d.id === created.driverId).emailVerified, false);
    const oldToken = lastToken();
    assert.equal((await request('POST', `/driver-accounts/${created.driverId}/resend`, {}, company)).status, 200);
    assert.equal((await activation(oldToken)).status, 422);
    const expiredToken = lastToken();
    await pool.query("UPDATE account_invitations SET expires_at=now()-interval '1 second' WHERE user_id=$1", [user.id]);
    assert.equal((await activation(expiredToken)).status, 422);
    assert.equal((await pool.query('SELECT is_active FROM users WHERE id=$1', [user.id])).rows[0].is_active, false);
    assert.equal((await request('POST', `/driver-accounts/${created.driverId}/resend`, {}, company)).status, 200);
    const validToken = lastToken();
    const stored = (await pool.query('SELECT token_hash,expires_at,created_at FROM account_invitations WHERE user_id=$1 AND used_at IS NULL', [user.id])).rows[0];
    assert.notEqual(stored.token_hash, validToken); assert.equal(Number(stored.expires_at) - Number(stored.created_at), 86400000);
    assert.equal((await request('PATCH', `/users/${user.id}/activate`, {}, company)).status, 403);
    await assert.rejects(pool.query('UPDATE users SET is_active=true WHERE id=$1', [user.id]));
    assert.equal((await activation(validToken, 'short')).status, 422);
    assert.equal((await activation(validToken)).status, 200);
    assert.ok((await pool.query('SELECT session_id FROM users WHERE id=$1', [user.id])).rows[0].session_id);
    assert.equal((await activation(validToken)).status, 422);
    assert.equal((await pool.query('SELECT status FROM drivers WHERE id=$1', [created.driverId])).rows[0].status, 'active');
    links = (await request('GET', '/driver-accounts/driver-links', undefined, company)).body.data;
    assert.equal(links.find((d: any) => d.id === created.driverId).emailVerified, true);
    assert.ok(links.find((d: any) => d.id === created.driverId).emailVerifiedAt);
    assert.equal((await request('POST', `/driver-accounts/${created.driverId}/resend`, {}, company)).status, 409);
    const login = await request('POST', '/auth/login', {username: profile.username, password: 'DriverSecure123!'});
    assert.equal(login.status, 200); assert.equal(login.body.data.user.role, 'Driver'); assert.ok(login.body.data.token);
    assert.equal((await request('POST', '/driver-accounts', {...profile, username: `other_${suffix}`, email: `other_${suffix}@example.invalid`, driverId: legacyDriver}, other)).status, 409);
    // Legacy accounts are unblocked independently of email proof; resend must
    // neither block them nor claim they have already verified their email.
    assert.equal(links.find((d: any) => d.id === legacyDriver).emailVerified, false);
    assert.equal((await request('POST', `/driver-accounts/${legacyDriver}/resend`, {}, company)).status, 200);
    const legacy = (await pool.query('SELECT is_active,invitation_pending,email_verified_at FROM users WHERE id=$1', [legacyUser])).rows[0];
    assert.deepEqual(legacy, {is_active: true, invitation_pending: false, email_verified_at: null});
    assert.equal((await activation(lastToken())).status, 200);
    assert.ok((await pool.query('SELECT email_verified_at FROM users WHERE id=$1', [legacyUser])).rows[0].email_verified_at);
    console.log('PASS: company creation without admin, duplicate races, mail rollback, tenant isolation, verification badges, company resend, expiry/revocation, activation/login and legacy account access.');
  } finally {
    try {
      const ids = (await pool.query('SELECT id FROM users WHERE username=$1', [profile.username])).rows.map(r => r.id);
      await pool.query('DELETE FROM account_invitations WHERE user_id=ANY($1::uuid[])', [[legacyUser, ...ids]]);
      await pool.query('DELETE FROM account_requests WHERE requested_by=ANY($1::uuid[])', [[admin, company, other]]);
      await pool.query('DELETE FROM drivers WHERE carrier_id=ANY($1::uuid[])', [[carrier, otherCarrier]]);
      await pool.query('DELETE FROM carrier_users WHERE carrier_id=ANY($1::uuid[])', [[carrier, otherCarrier]]);
      await pool.query('DELETE FROM carriers WHERE id=ANY($1::uuid[])', [[carrier, otherCarrier]]);
      await pool.query('DELETE FROM users WHERE id=ANY($1::uuid[])', [[admin, company, other, legacyUser, ...ids]]);
    } finally { await close(server); await close(emailServer); }
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => pool.end());

import { createHash, randomBytes } from 'crypto';
import bcrypt from 'bcryptjs';
import { z } from 'zod';
import { getClient, query } from '../../../infrastructure/database/db';
import { ConflictError, ForbiddenError, ValidationError } from '../../../shared/errors/app-error';

const optionalText = z.string().trim().max(500).refine(value => !value || /^https?:\/\//i.test(value) || /^NexusPort\/drivers\//.test(value), 'Ảnh phải là URL HTTP(S) hoặc ảnh tài xế đã tải lên.').nullable().optional();
export const driverRequestSchema = z.object({
 username: z.string().trim().regex(/^[a-zA-Z0-9_.-]{3,100}$/),
 email: z.string().trim().email().max(254).transform(v => v.toLowerCase()),
 driverId: z.string().uuid().optional(),
 fullName: z.string().trim().min(2).max(150),
 phone: z.string().trim().regex(/^\+?[0-9]{9,15}$/),
 idCardNumber: z.string().regex(/^[0-9]{12}$/),
 licenseNumber: z.string().trim().min(1).max(80),
 photoUrl: optionalText, idCardFrontUrl: optionalText, idCardBackUrl: optionalText,
 licenseImageUrl: optionalText, licenseBackImageUrl: optionalText,
 idCardExpiryDate: z.string().datetime().nullable().optional(),
 licenseExpiryDate: z.string().datetime().nullable().optional(),
}).strict();
export const activationSchema = z.object({
 token: z.string().regex(/^[a-f0-9]{64}$/),
 password: z.string().min(10).max(72),
}).strict();
export const hashInvitation = (token: string) => createHash('sha256').update(token).digest('hex');
const parse = <T>(schema: z.ZodType<T>, input: unknown): T => {
 const result = schema.safeParse(input);
 if (!result.success) throw new ValidationError('Vui lòng kiểm tra tên đăng nhập, email và hồ sơ tài xế.');
 return result.data;
};
export class AccountRequestService {
 async submit(actorId: string, input: unknown) {
  const data = parse(driverRequestSchema, input);
  const client = await getClient();
  try {
   await client.query('BEGIN');
   const membership = await client.query(`SELECT cu.carrier_id FROM carrier_users cu JOIN users u ON u.id=cu.user_id
    WHERE u.id=$1 AND u.role='Transport Company' AND u.is_active=true FOR SHARE OF u`, [actorId]);
   if (membership.rows.length !== 1) throw new ForbiddenError('Tài khoản phải thuộc đúng một công ty vận tải.');
   const carrierId = membership.rows[0].carrier_id;
   if (data.driverId) {
    const driver = await client.query('SELECT id,user_id FROM drivers WHERE id=$1 AND carrier_id=$2 FOR UPDATE', [data.driverId,carrierId]);
    if (!driver.rowCount || driver.rows[0].user_id) throw new ConflictError('Hồ sơ không thuộc công ty hoặc đã có tài khoản.');
   }
   if ((await client.query(`SELECT 1 FROM users WHERE lower(username)=lower($1) OR lower(email)=lower($2)
    OR lower(username)=lower($2) OR lower(email)=lower($1)`, [data.username,data.email])).rowCount)
    throw new ConflictError('Tên đăng nhập hoặc email đã được sử dụng.');
   const result = await client.query(`INSERT INTO account_requests(role,carrier_id,driver_id,requested_by,username,email,profile)
    VALUES('Driver',$1,$2,$3,$4,$5,$6) RETURNING id,status,created_at`,
    [carrierId,data.driverId || null,actorId,data.username,data.email,JSON.stringify(data)]);
   await client.query('COMMIT');
   return result.rows[0];
  } catch (error) { await client.query('ROLLBACK'); if ((error as {code?:string}).code==='23505') throw new ConflictError('Yêu cầu cho tài xế, tên đăng nhập hoặc email này đang chờ duyệt.'); throw error; }
  finally { client.release(); }
 }
 async list(actorId: string, admin: boolean) {
  return (await query(`SELECT r.*, c.company_name,u.invitation_pending FROM account_requests r JOIN carriers c ON c.id=r.carrier_id LEFT JOIN users u ON u.id=r.user_id
   WHERE $2::boolean OR EXISTS(SELECT 1 FROM carrier_users cu WHERE cu.user_id=$1 AND cu.carrier_id=r.carrier_id)
   ORDER BY r.created_at DESC LIMIT 200`,[actorId,admin])).rows;
 }
 async review(actorId: string, id: string, approve: boolean) {
  const client = await getClient();
  try {
   await client.query('BEGIN');
   // Serialize approvals so identity checks cannot race other approvals.
   await client.query("SELECT pg_advisory_xact_lock(hashtext('account-request-approval'))");
   const row = (await client.query('SELECT * FROM account_requests WHERE id=$1 FOR UPDATE',[id])).rows[0];
   if (!row || row.status!=='pending') throw new ConflictError('Yêu cầu không còn chờ duyệt.');
   if (!approve) {
    await client.query("UPDATE account_requests SET status='rejected',reviewed_by=$2,reviewed_at=now() WHERE id=$1",[id,actorId]);
    await client.query('COMMIT'); return {status:'rejected'};
   }
   const data = parse(driverRequestSchema,row.profile);
   if ((await client.query(`SELECT 1 FROM users WHERE lower(username)=lower($1) OR lower(email)=lower($2)
    OR lower(username)=lower($2) OR lower(email)=lower($1)`,[row.username,row.email])).rowCount)
    throw new ConflictError('Tên đăng nhập hoặc email đã được sử dụng.');
   const user = (await client.query(`INSERT INTO users(id,username,email,password,role,full_name,is_active,invitation_pending,created_at,updated_at)
    VALUES(gen_random_uuid(),$1,$2,$3,'Driver',$4,false,true,now(),now()) RETURNING id`,
    [row.username,row.email,await bcrypt.hash(randomBytes(32).toString('hex'),12),data.fullName])).rows[0];
   let driverId = row.driver_id;
   if (driverId) {
    const linked = await client.query("UPDATE drivers SET user_id=$1 WHERE id=$2 AND carrier_id=$3 AND user_id IS NULL RETURNING id",[user.id,driverId,row.carrier_id]);
    if (!linked.rowCount) throw new ConflictError('Hồ sơ đã được liên kết hoặc đã đổi công ty.');
   } else {
    driverId = (await client.query(`INSERT INTO drivers(id,carrier_id,user_id,full_name,phone,id_card_number,license_number,
     photo_url,id_card_front_url,id_card_back_url,license_image_url,license_back_image_url,id_card_expiry_date,license_expiry_date,status,created_at)
     VALUES(gen_random_uuid(),$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,'inactive',now()) RETURNING id`,
     [row.carrier_id,user.id,data.fullName,data.phone,data.idCardNumber,data.licenseNumber,data.photoUrl,data.idCardFrontUrl,data.idCardBackUrl,
      data.licenseImageUrl,data.licenseBackImageUrl,data.idCardExpiryDate,data.licenseExpiryDate])).rows[0].id;
   }
   await client.query('INSERT INTO carrier_users(carrier_id,user_id) VALUES($1,$2) ON CONFLICT DO NOTHING',[row.carrier_id,user.id]);
   await client.query("UPDATE account_requests SET status='approved',reviewed_by=$2,reviewed_at=now(),user_id=$3,driver_id=$4 WHERE id=$1",[id,actorId,user.id,driverId]);
   await this.invite(client,id,user.id,row.email);
   await client.query('COMMIT'); return {status:'approved'};
  } catch(error) { await client.query('ROLLBACK'); if ((error as {code?:string}).code==='23505') throw new ConflictError('Thông tin tài xế hoặc tài khoản đã tồn tại.'); throw error; }
  finally { client.release(); }
 }
 private async invite(client: import('pg').PoolClient, requestId: string, userId: string, email: string) {
  const token = randomBytes(32).toString('hex');
  const origin = process.env.ACCOUNT_ACTIVATION_ORIGIN;
  const endpoint = process.env.ACCOUNT_EMAIL_SERVICE_URL;
  const key = process.env.ACCOUNT_EMAIL_SERVICE_KEY;
  if (!origin || !endpoint || !key) throw new ValidationError('Chưa cấu hình dịch vụ gửi email kích hoạt.');
  const url = new URL('/activate-account',origin); url.hash = new URLSearchParams({token}).toString();
  await client.query('UPDATE account_invitations SET used_at=now() WHERE user_id=$1 AND used_at IS NULL',[userId]);
  await client.query(`INSERT INTO account_invitations(user_id,request_id,token_hash,expires_at) VALUES($1,$2,$3,now()+interval '1 day')`,[userId,requestId,hashInvitation(token)]);
  const response = await fetch(endpoint,{method:'POST',headers:{'Content-Type':'application/json','X-Account-Email-Key':key},
   body:JSON.stringify({to:email,activationUrl:url.toString()}),signal:AbortSignal.timeout(20000)});
  if (!response.ok) throw new ValidationError('Không gửi được email kích hoạt. Tài khoản vẫn bị chặn. Vui lòng thử lại.');
 }
 async resend(id: string) {
  const client = await getClient();
  try {
   await client.query('BEGIN');
   const row = (await client.query(`SELECT r.*,u.invitation_pending FROM account_requests r JOIN users u ON u.id=r.user_id WHERE r.id=$1 FOR UPDATE OF r,u`,[id])).rows[0];
   if (!row || row.status!=='approved' || !row.invitation_pending) throw new ConflictError('Tài khoản không còn chờ kích hoạt.');
   await this.invite(client,id,row.user_id,row.email); await client.query('COMMIT'); return {sent:true};
  } catch(error) { await client.query('ROLLBACK'); throw error; } finally {client.release();}
 }
 async activate(input: unknown) {
  const data = parse(activationSchema,input);
  const password = await bcrypt.hash(data.password,12);
  const client = await getClient();
  try {
   await client.query('BEGIN');
   const invitation = (await client.query(`SELECT i.id,i.user_id FROM account_invitations i JOIN users u ON u.id=i.user_id
    WHERE token_hash=$1 AND used_at IS NULL AND expires_at>now() AND u.invitation_pending AND u.role='Driver' FOR UPDATE OF i,u`,[hashInvitation(data.token)])).rows[0];
   if (!invitation) throw new ValidationError('Liên kết không hợp lệ, đã dùng hoặc đã hết hạn 24 giờ.');
   await client.query('UPDATE users SET password=$2,invitation_pending=false,is_active=true,session_id=NULL,updated_at=now() WHERE id=$1',[invitation.user_id,password]);
   await client.query(`UPDATE drivers d SET status='active' WHERE d.user_id=$1 AND d.status='inactive' AND EXISTS(
    SELECT 1 FROM account_requests r WHERE r.user_id=$1 AND r.status='approved' AND NOT (r.profile ? 'driverId'))`,[invitation.user_id]);
   await client.query('UPDATE account_invitations SET used_at=now() WHERE user_id=$1 AND used_at IS NULL',[invitation.user_id]);
   await client.query('COMMIT'); return {activated:true};
  } catch(error) {await client.query('ROLLBACK');throw error;} finally {client.release();}
 }
}

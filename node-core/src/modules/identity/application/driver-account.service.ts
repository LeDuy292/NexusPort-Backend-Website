import { createHash, randomBytes } from 'crypto';
import bcrypt from 'bcryptjs';
import { PoolClient } from 'pg';
import { z } from 'zod';
import { getClient, query } from '../../../infrastructure/database/db';
import { ConflictError, ForbiddenError, NotFoundError, ValidationError } from '../../../shared/errors/app-error';

const image = z.string().trim().max(500).refine(value => !value || /^https?:\/\//i.test(value) || /^NexusPort\/drivers\//.test(value), 'Ảnh phải là ảnh tài xế đã tải lên.').nullable().optional();
export const driverAccountSchema = z.object({
  username: z.string().trim().regex(/^[a-zA-Z0-9_.-]{3,100}$/),
  email: z.string().trim().email().max(254).transform(value => value.toLowerCase()),
  driverId: z.string().uuid().optional(),
  fullName: z.string().trim().min(2).max(150),
  phone: z.string().trim().regex(/^\+?[0-9]{9,15}$/),
  idCardNumber: z.string().regex(/^[0-9]{12}$/),
  licenseNumber: z.string().trim().min(1).max(80),
  photoUrl: image, idCardFrontUrl: image, idCardBackUrl: image,
  licenseImageUrl: image, licenseBackImageUrl: image,
  idCardExpiryDate: z.string().datetime().nullable().optional(),
  licenseExpiryDate: z.string().datetime().nullable().optional(),
}).strict();
export const activationSchema = z.object({
  token: z.string().regex(/^[a-f0-9]{64}$/),
  password: z.string().min(10).max(72).refine(value => Buffer.byteLength(value, 'utf8') <= 72),
}).strict();
const hashInvitation = (token: string) => createHash('sha256').update(token).digest('hex');
function parse<T>(schema: z.ZodType<T>, input: unknown): T {
  const result = schema.safeParse(input);
  if (!result.success) throw new ValidationError('Vui lòng kiểm tra thông tin tài khoản và hồ sơ tài xế.');
  return result.data;
}

export class DriverAccountService {
  async create(actorId: string, input: unknown) {
    const data = parse(driverAccountSchema, input);
    const passwordHash = await bcrypt.hash(randomBytes(32).toString('hex'), 12);
    const client = await getClient();
    try {
      await client.query('BEGIN');
      await client.query("SELECT pg_advisory_xact_lock(hashtext('driver-account-provisioning'))");
      const memberships = await client.query(`SELECT cu.carrier_id FROM carrier_users cu JOIN users u ON u.id=cu.user_id
        WHERE u.id=$1 AND u.role='Transport Company' AND u.is_active FOR SHARE OF u`, [actorId]);
      if (memberships.rows.length !== 1) throw new ForbiddenError('Tài khoản phải thuộc đúng một công ty vận tải.');
      const carrierId = memberships.rows[0].carrier_id;
      if (data.driverId) {
        const driver = await client.query('SELECT id,user_id FROM drivers WHERE id=$1 AND carrier_id=$2 FOR UPDATE', [data.driverId, carrierId]);
        if (!driver.rowCount || driver.rows[0].user_id) throw new ConflictError('Hồ sơ không thuộc công ty hoặc đã có tài khoản.');
      }
      if ((await client.query(`SELECT 1 FROM users WHERE lower(username)=lower($1) OR lower(email)=lower($2)
        OR lower(username)=lower($2) OR lower(email)=lower($1)`, [data.username, data.email])).rowCount)
        throw new ConflictError('Tên đăng nhập hoặc email đã được sử dụng.');

      const userId = (await client.query(`INSERT INTO users(id,username,email,password,role,full_name,is_active,invitation_pending,created_at,updated_at)
        VALUES(gen_random_uuid(),$1,$2,$3,'Driver',$4,false,true,now(),now()) RETURNING id`,
      [data.username, data.email, passwordHash, data.fullName])).rows[0].id;
      let driverId = data.driverId;
      if (driverId) {
        const linked = await client.query(`UPDATE drivers SET user_id=$1 WHERE id=$2 AND carrier_id=$3 AND user_id IS NULL RETURNING id`, [userId, driverId, carrierId]);
        if (!linked.rowCount) throw new ConflictError('Hồ sơ đã được liên kết hoặc đã đổi công ty.');
      } else {
        driverId = (await client.query(`INSERT INTO drivers(id,carrier_id,user_id,full_name,phone,id_card_number,license_number,
          photo_url,id_card_front_url,id_card_back_url,license_image_url,license_back_image_url,id_card_expiry_date,license_expiry_date,status,created_at)
          VALUES(gen_random_uuid(),$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,'inactive',now()) RETURNING id`,
        [carrierId, userId, data.fullName, data.phone, data.idCardNumber, data.licenseNumber, data.photoUrl,
          data.idCardFrontUrl, data.idCardBackUrl, data.licenseImageUrl, data.licenseBackImageUrl, data.idCardExpiryDate, data.licenseExpiryDate])).rows[0].id;
      }
      await client.query('INSERT INTO carrier_users(carrier_id,user_id) VALUES($1,$2) ON CONFLICT DO NOTHING', [carrierId, userId]);
      // Retain the previous table as the provisioning audit/invitation parent.
      // No request is queued and no administrator reviews the account.
      const recordId = (await client.query(`INSERT INTO account_requests(role,carrier_id,driver_id,requested_by,username,email,profile,status,user_id)
        VALUES('Driver',$1,$2,$3,$4,$5,$6,'approved',$7) RETURNING id`,
      [carrierId, driverId, actorId, data.username, data.email, JSON.stringify(data), userId])).rows[0].id;
      const expiresAt = await this.invite(client, recordId, userId, data.email);
      await client.query('COMMIT');
      return { driverId, userId, username: data.username, email: data.email, emailVerified: false, invitationPending: true, expiresAt };
    } catch (error) {
      await client.query('ROLLBACK');
      if ((error as {code?: string}).code === '23505') throw new ConflictError('Thông tin tài xế hoặc tài khoản đã tồn tại.');
      throw error;
    } finally { client.release(); }
  }

  async links(actorId: string, admin: boolean) {
    return (await query(`SELECT d.id,d.user_id AS "userId",u.username,u.email,
      u.invitation_pending AS "invitationPending",u.email_verified_at AS "emailVerifiedAt",
      (u.email_verified_at IS NOT NULL) AS "emailVerified",
      (SELECT i.expires_at FROM account_invitations i WHERE i.user_id=u.id AND i.used_at IS NULL
        ORDER BY i.created_at DESC LIMIT 1) AS "invitationExpiresAt"
      FROM drivers d LEFT JOIN users u ON u.id=d.user_id WHERE $2::boolean OR EXISTS(
        SELECT 1 FROM carrier_users cu WHERE cu.user_id=$1 AND cu.carrier_id=d.carrier_id)`, [actorId, admin])).rows;
  }

  private async invite(client: PoolClient, recordId: string, userId: string, email: string) {
    const origin = process.env.ACCOUNT_ACTIVATION_ORIGIN;
    const endpoint = process.env.ACCOUNT_EMAIL_SERVICE_URL;
    const key = process.env.ACCOUNT_EMAIL_SERVICE_KEY;
    if (!origin || !endpoint || !key) throw new ValidationError('Chưa cấu hình dịch vụ gửi email xác nhận.');
    const token = randomBytes(32).toString('hex');
    const url = new URL('/activate-account', origin);
    url.hash = new URLSearchParams({token}).toString();
    await client.query('UPDATE account_invitations SET used_at=now() WHERE user_id=$1 AND used_at IS NULL', [userId]);
    const expiresAt = (await client.query(`INSERT INTO account_invitations(user_id,request_id,token_hash,expires_at)
      VALUES($1,$2,$3,now()+interval '1 day') RETURNING expires_at`, [userId, recordId, hashInvitation(token)])).rows[0].expires_at;
    try {
      const response = await fetch(endpoint, {method: 'POST', headers: {'Content-Type': 'application/json', 'X-Account-Email-Key': key},
        body: JSON.stringify({to: email, activationUrl: url.toString()}), signal: AbortSignal.timeout(20000)});
      if (!response.ok) throw new Error('Email delivery failed');
    } catch {
      throw new ValidationError('Không gửi được email xác nhận. Vui lòng thử lại.');
    }
    return expiresAt;
  }

  async resend(actorId: string, driverId: string, admin: boolean) {
    const client = await getClient();
    try {
      await client.query('BEGIN');
      const row = (await client.query(`SELECT d.carrier_id,d.full_name,d.phone,d.id_card_number,d.license_number,
        u.id AS user_id,u.username,u.email,u.email_verified_at,u.invitation_pending,u.is_active
        FROM drivers d JOIN users u ON u.id=d.user_id WHERE d.id=$1 AND u.role='Driver' AND ($3::boolean OR EXISTS(
          SELECT 1 FROM carrier_users cu WHERE cu.user_id=$2 AND cu.carrier_id=d.carrier_id)) FOR UPDATE OF u`, [driverId, actorId, admin])).rows[0];
      if (!row) throw new NotFoundError('Tài xế');
      if (row.email_verified_at) throw new ConflictError('Email tài xế đã được xác minh.');
      if (!row.is_active && !row.invitation_pending) throw new ConflictError('Tài khoản đã bị chặn bởi quản trị viên.');
      let recordId = (await client.query(`SELECT id FROM account_requests WHERE user_id=$1 AND status='approved' AND lower(email)=lower($2)
        ORDER BY created_at DESC LIMIT 1`, [row.user_id, row.email])).rows[0]?.id;
      if (!recordId) {
        const profile = {username: row.username, email: row.email, driverId, fullName: row.full_name,
          phone: row.phone, idCardNumber: row.id_card_number, licenseNumber: row.license_number};
        recordId = (await client.query(`INSERT INTO account_requests(role,carrier_id,driver_id,requested_by,username,email,profile,status,user_id)
          VALUES('Driver',$1,$2,$3,$4,$5,$6,'approved',$7) RETURNING id`,
        [row.carrier_id, driverId, actorId, row.username, row.email, JSON.stringify(profile), row.user_id])).rows[0].id;
      }
      const expiresAt = await this.invite(client, recordId, row.user_id, row.email);
      await client.query('COMMIT');
      return {sent: true, expiresAt};
    } catch (error) { await client.query('ROLLBACK'); throw error; }
    finally { client.release(); }
  }

  async activate(input: unknown) {
    const data = parse(activationSchema, input);
    const password = await bcrypt.hash(data.password, 12);
    const client = await getClient();
    try {
      await client.query('BEGIN');
      const invitation = (await client.query(`SELECT i.id,i.user_id FROM account_invitations i JOIN users u ON u.id=i.user_id
        JOIN account_requests r ON r.id=i.request_id AND r.user_id=u.id AND lower(r.email)=lower(u.email)
        WHERE i.token_hash=$1 AND i.used_at IS NULL AND i.expires_at>now() AND u.role='Driver'
          AND (u.invitation_pending OR (u.is_active AND u.email_verified_at IS NULL)) FOR UPDATE OF i,u`, [hashInvitation(data.token)])).rows[0];
      if (!invitation) throw new ValidationError('Liên kết không hợp lệ, đã dùng hoặc đã hết hạn 24 giờ.');
      await client.query(`UPDATE users SET password=$2,invitation_pending=false,is_active=true,email_verified_at=now(),
        session_id=gen_random_uuid()::text,updated_at=now() WHERE id=$1`, [invitation.user_id, password]);
      await client.query(`UPDATE drivers d SET status='active' WHERE d.user_id=$1 AND d.status='inactive' AND EXISTS(
        SELECT 1 FROM account_requests r WHERE r.user_id=$1 AND r.status='approved' AND NOT (r.profile ? 'driverId'))`, [invitation.user_id]);
      await client.query('UPDATE account_invitations SET used_at=now() WHERE user_id=$1 AND used_at IS NULL', [invitation.user_id]);
      await client.query('COMMIT');
      return {activated: true, emailVerified: true};
    } catch (error) { await client.query('ROLLBACK'); throw error; }
    finally { client.release(); }
  }
}

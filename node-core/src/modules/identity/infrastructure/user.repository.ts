import { query } from '../../../infrastructure/database/db';
import { DbUser, CarrierStaffUser } from '../domain/user.types';

export class UserRepository {
  async findByUsernameOrEmail(identifier: string): Promise<DbUser | null> {
    const res = await query(
      `SELECT id, username, email, password, role, full_name, is_active,
              reset_token, reset_token_exp, created_at, updated_at
         FROM users
        WHERE LOWER(username) = LOWER($1) OR LOWER(email) = LOWER($1)
        LIMIT 1`,
      [identifier.trim()]
    );
    return (res.rows[0] as DbUser) || null;
  }

  async findById(id: string): Promise<DbUser | null> {
    const res = await query(
      `SELECT id, username, email, password, role, full_name, is_active,
              reset_token, reset_token_exp, created_at, updated_at
         FROM users
        WHERE id = $1
        LIMIT 1`,
      [id]
    );
    return (res.rows[0] as DbUser) || null;
  }

  async findCarrierIdByUserId(userId: string): Promise<string | null> {
    const res = await query(
      `SELECT carrier_id FROM carrier_users WHERE user_id = $1 LIMIT 1`,
      [userId]
    );
    return res.rows[0]?.carrier_id || null;
  }

  async createUser(data: {
    username: string;
    email: string;
    passwordHash: string;
    role: string;
    fullName?: string;
    isActive?: boolean;
  }): Promise<DbUser> {
    const res = await query(
      `INSERT INTO users (id, username, email, password, role, full_name, is_active, created_at, updated_at)
       VALUES (gen_random_uuid(), $1, $2, $3, $4, $5, $6, NOW(), NOW())
       RETURNING id, username, email, role, full_name, is_active, created_at, updated_at`,
      [
        data.username.trim(),
        data.email.trim().toLowerCase(),
        data.passwordHash,
        data.role,
        data.fullName || null,
        data.isActive !== undefined ? data.isActive : true,
      ]
    );
    return res.rows[0] as DbUser;
  }

  async linkCarrierUser(carrierId: string, userId: string): Promise<void> {
    await query(
      `INSERT INTO carrier_users (carrier_id, user_id)
       VALUES ($1, $2)
       ON CONFLICT DO NOTHING`,
      [carrierId, userId]
    );
  }

  async getCarrierStaffs(carrierId: string): Promise<CarrierStaffUser[]> {
    const res = await query(
      `SELECT u.id, u.username, u.email, u.full_name AS "fullName",
              u.is_active AS "isActive", u.created_at AS "createdAt"
         FROM users u
         JOIN carrier_users cu ON u.id = cu.user_id
        WHERE cu.carrier_id = $1 AND u.role = 'Carrier Staff'
        ORDER BY u.created_at DESC`,
      [carrierId]
    );
    return res.rows as CarrierStaffUser[];
  }

  async updateActiveStatus(id: string, isActive: boolean): Promise<DbUser | null> {
    const res = await query(
      `UPDATE users
          SET is_active = $2, updated_at = NOW()
        WHERE id = $1
        RETURNING id, username, email, role, full_name, is_active, created_at, updated_at`,
      [id, isActive]
    );
    return (res.rows[0] as DbUser) || null;
  }

  async listUsers(options: {
    search?: string;
    role?: string;
    status?: string;
    page?: number;
    limit?: number;
  }): Promise<{ users: DbUser[]; total: number; page: number; limit: number; totalPages: number }> {
    const page = Math.max(1, options.page || 1);
    const limit = Math.min(100, Math.max(1, options.limit || 20));
    const offset = (page - 1) * limit;

    const conditions: string[] = [];
    const params: unknown[] = [];
    let paramIndex = 1;

    if (options.search && options.search.trim()) {
      conditions.push(`(username ILIKE $${paramIndex} OR email ILIKE $${paramIndex} OR full_name ILIKE $${paramIndex})`);
      params.push(`%${options.search.trim()}%`);
      paramIndex++;
    }

    if (options.role && options.role !== 'all') {
      conditions.push(`role = $${paramIndex}`);
      params.push(options.role);
      paramIndex++;
    }

    if (options.status === 'active') {
      conditions.push(`is_active = true`);
    } else if (options.status === 'inactive') {
      conditions.push(`is_active = false`);
    }

    const whereClause = conditions.length > 0 ? `WHERE ${conditions.join(' AND ')}` : '';

    const countRes = await query(`SELECT COUNT(*)::int AS count FROM users ${whereClause}`, params);
    const total = countRes.rows[0]?.count || 0;

    const listParams = [...params, limit, offset];
    const usersRes = await query(
      `SELECT id, username, email, role, full_name, is_active, created_at, updated_at
         FROM users
         ${whereClause}
        ORDER BY created_at DESC
        LIMIT $${paramIndex} OFFSET $${paramIndex + 1}`,
      listParams
    );

    return {
      users: usersRes.rows as DbUser[],
      total,
      page,
      limit,
      totalPages: Math.ceil(total / limit),
    };
  }
}

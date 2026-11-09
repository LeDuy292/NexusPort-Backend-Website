import { getClient, query } from '../../../infrastructure/database/db';
import { AppError, ForbiddenError } from '../../../shared/errors/app-error';

interface ShippingLineInput {
  fullName: string;
  shortName: string;
  prefixes: string[];
  userIds: string[];
}

const directory = `SELECT l.id,l.full_name AS "fullName",l.short_name AS "shortName",
  ARRAY(SELECT p.prefix FROM shipping_line_prefixes p WHERE p.shipping_line_id=l.id ORDER BY p.prefix) AS prefixes
  FROM shipping_lines l`;

export class ShippingLineDirectoryRepository {
  async list() {
    return (await query(`${directory} ORDER BY l.short_name,l.id`)).rows;
  }

  async own(userId: string) {
    const result = await query(`${directory} JOIN shipping_line_accounts a ON a.shipping_line_id=l.id
      JOIN users u ON u.id=a.user_id
      WHERE u.id=$1 AND u.role='Shipping Line' AND u.is_active`, [userId]);
    return result.rows[0] ?? null;
  }

  async accounts(adminId: string) {
    if (!(await query("SELECT 1 FROM users WHERE id=$1 AND role='Administrator' AND is_active", [adminId])).rowCount)
      throw new ForbiddenError('Cần tài khoản quản trị đang hoạt động.');
    return (await query(`SELECT u.id,u.username,u.full_name AS "fullName",a.shipping_line_id AS "shippingLineId",u.is_active AS "isActive"
      FROM users u LEFT JOIN shipping_line_accounts a ON a.user_id=u.id
      WHERE u.role='Shipping Line' ORDER BY u.username`)).rows;
  }

  async save(input: ShippingLineInput, adminId: string, id?: string) {
    const client = await getClient();
    try {
      await client.query('BEGIN');
      const admin = await client.query("SELECT 1 FROM users WHERE id=$1 AND role='Administrator' AND is_active FOR SHARE", [adminId]);
      if (!admin.rowCount) throw new ForbiddenError('Cần tài khoản quản trị đang hoạt động.');
      const names = [input.fullName, input.shortName];
      const result = id
        ? await client.query('UPDATE shipping_lines SET full_name=$1,short_name=$2 WHERE id=$3 RETURNING id', [...names, id])
        : await client.query('INSERT INTO shipping_lines(full_name,short_name) VALUES($1,$2) RETURNING id', names);
      const lineId = result.rows[0]?.id;
      if (!lineId) throw new AppError('Không tìm thấy hãng tàu.', 404);

      const accounts = await client.query(`SELECT id FROM users
        WHERE id=ANY($1::uuid[]) AND role='Shipping Line' ORDER BY id FOR UPDATE`, [input.userIds]);
      const conflict = await client.query(`SELECT 1 FROM shipping_line_accounts
        WHERE user_id=ANY($1::uuid[]) AND shipping_line_id<>$2 LIMIT 1`, [input.userIds, lineId]);
      if (accounts.rows.length !== input.userIds.length || conflict.rowCount)
        throw new AppError('Tài khoản phải có vai trò Hãng tàu và chỉ thuộc một hãng.', 409);
      await client.query('DELETE FROM shipping_line_accounts WHERE shipping_line_id=$1 AND NOT (user_id=ANY($2::uuid[]))', [lineId, input.userIds]);
      await client.query(`INSERT INTO shipping_line_accounts(user_id,shipping_line_id)
        SELECT u,$1 FROM unnest($2::uuid[]) AS u ON CONFLICT (user_id) DO NOTHING`, [lineId, input.userIds]);
      await client.query('DELETE FROM shipping_line_prefixes WHERE shipping_line_id=$1 AND NOT (prefix=ANY($2::varchar[]))', [lineId, input.prefixes]);
      await client.query(`INSERT INTO shipping_line_prefixes(prefix,shipping_line_id)
        SELECT p,$1 FROM unnest($2::varchar[]) AS p
        WHERE NOT EXISTS(SELECT 1 FROM shipping_line_prefixes WHERE prefix=p AND shipping_line_id=$1)
        ORDER BY p`, [lineId, input.prefixes]);
      await client.query('COMMIT');
      return { id: lineId, ...input };
    } catch (error) {
      await client.query('ROLLBACK');
      if ((error as { code?: string }).code === '23505')
        throw new AppError('Tên viết tắt hoặc tiền tố đã được đăng ký cho một hãng tàu.', 409);
      throw error;
    } finally {
      client.release();
    }
  }
}

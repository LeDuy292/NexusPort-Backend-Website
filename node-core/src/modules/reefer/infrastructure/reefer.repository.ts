import { PoolClient } from 'pg';
import pool, { query } from '../../../infrastructure/database/db';
import {
  ReeferPlug,
  ReeferPowerRequest,
  ReeferPowerSession,
  ReeferTemperatureRecord,
  ReeferAlert,
  ReeferOverviewStats,
  SocketUsageStatus,
  SocketTechnicalStatus,
  PowerSessionStatus,
  ActualPowerStatus,
  ReeferRequestStatus,
  ReeferAlertStatus,
  AlertSeverity,
  TempDataSource
} from '../domain/reefer.types';

export class ReeferRepository {
  /**
   * Lấy tổng quan KPIs thống kê phân hệ Reefer
   */
  async getOverviewStats(): Promise<ReeferOverviewStats> {
    // Tự động kiểm tra và giải phóng ổ cắm nếu container đã xuất bãi / rời cảng
    try {
      await this.syncDepartedContainers();
    } catch (e) {
      // Ignored
    }

    const plugStatsRes = await query(`
      SELECT 
        COUNT(*)::int AS total_plugs,
        COUNT(*) FILTER (WHERE usage_status = 'AVAILABLE' AND technical_status = 'HEALTHY')::int AS available_plugs,
        COUNT(*) FILTER (WHERE usage_status = 'OCCUPIED')::int AS occupied_plugs,
        COUNT(*) FILTER (WHERE usage_status = 'RESERVED')::int AS reserved_plugs,
        COUNT(*) FILTER (WHERE technical_status IN ('FAULTY', 'UNDER_MAINTENANCE'))::int AS faulty_plugs
      FROM public.reefer_plugs;
    `);

    const sessionStatsRes = await query(`
      SELECT 
        COUNT(*) FILTER (WHERE session_status = 'ACTIVE')::int AS active_sessions,
        COUNT(*) FILTER (WHERE session_status = 'AWAITING_POWER')::int AS awaiting_power_sessions
      FROM public.reefer_power_sessions;
    `);

    const reqStatsRes = await query(`
      SELECT COUNT(*)::int AS pending_requests
      FROM public.reefer_power_requests
      WHERE status IN ('PENDING_ASSIGNMENT', 'ASSIGNED');
    `);

    const alertStatsRes = await query(`
      SELECT COUNT(*)::int AS active_alerts
      FROM public.reefer_alerts
      WHERE status IN ('TRIGGERED', 'ACKNOWLEDGED', 'IN_PROGRESS');
    `);

    const plugs = plugStatsRes.rows[0] || {};
    const sessions = sessionStatsRes.rows[0] || {};
    const requests = reqStatsRes.rows[0] || {};
    const alerts = alertStatsRes.rows[0] || {};

    return {
      total_plugs: plugs.total_plugs || 0,
      available_plugs: plugs.available_plugs || 0,
      occupied_plugs: plugs.occupied_plugs || 0,
      reserved_plugs: plugs.reserved_plugs || 0,
      faulty_plugs: plugs.faulty_plugs || 0,
      active_sessions: sessions.active_sessions || 0,
      awaiting_power_sessions: sessions.awaiting_power_sessions || 0,
      pending_requests: requests.pending_requests || 0,
      active_alerts: alerts.active_alerts || 0,
    };
  }

  /**
   * Danh sách container đã nhập cảng chưa được cấp điện (để hiển thị chọn, không cần gõ tay)
   */
  async getEligibleContainers(): Promise<any[]> {
    const res = await query(`
      SELECT 
        c.id,
        c.container_no,
        c.cargo_type,
        c.status,
        c.is_reefer,
        ct.code AS container_type_code,
        ct.category AS container_category,
        rpr.id AS pending_request_id,
        rpr.target_temperature,
        rpr.temp_tolerance
      FROM public.containers c
      LEFT JOIN public.container_types ct ON c.container_type_id = ct.id
      LEFT JOIN public.reefer_power_requests rpr 
        ON c.id = rpr.container_id AND rpr.status = 'PENDING_ASSIGNMENT'
      WHERE c.left_at IS NULL
        AND c.status::text NOT IN ('departed', 'gate_out', 'cancelled')
        AND (
          c.is_reefer = TRUE 
          OR ct.category = 'reefer' 
          OR ct.code ILIKE '%RF%' 
          OR c.cargo_type = 'perishable'
        )
        AND c.id NOT IN (
          SELECT container_id 
          FROM public.reefer_power_sessions 
          WHERE session_status IN ('AWAITING_POWER', 'ACTIVE')
        )
      ORDER BY c.container_no ASC;
    `);
    return res.rows;
  }

  /**
   * Danh sách ổ cắm Reefer Plugs
   */
  async getPlugs(filters?: {
    usage_status?: SocketUsageStatus;
    technical_status?: SocketTechnicalStatus;
    search?: string;
  }): Promise<ReeferPlug[]> {
    let sql = `
      SELECT 
        rp.id,
        rp.slot_id,
        rp.plug_code,
        rp.is_available,
        rp.is_power_on,
        rp.usage_status,
        rp.technical_status,
        rp.voltage_type,
        rp.max_power_kw,
        rp.last_maintained_at,
        rp.created_at,
        rp.updated_at,
        CASE WHEN ys.id IS NOT NULL THEN CONCAT(COALESCE(yb.code, 'R'), '-', LPAD(ys.bay::text, 2, '0'), '-', ys.row_no, '-', ys.tier) ELSE NULL END AS slot_code,
        yb.code AS block_code,
        rps.id AS active_session_id,
        rps.container_id AS active_container_id,
        c.container_no AS active_container_no,
        rps.session_status AS active_session_status,
        rps.actual_power_status AS active_power_status,
        rps.started_at AS active_started_at,
        rps.booking_code AS active_booking_code,
        rps.required_power_kw AS active_required_power_kw,
        rpr.target_temperature,
        rpr.temp_tolerance
      FROM public.reefer_plugs rp
      LEFT JOIN public.yard_slots ys ON rp.slot_id = ys.id
      LEFT JOIN public.yard_blocks yb ON ys.block_id = yb.id
      LEFT JOIN public.reefer_power_sessions rps ON rp.id = rps.plug_id AND rps.session_status IN ('AWAITING_POWER', 'ACTIVE')
      LEFT JOIN public.containers c ON rps.container_id = c.id
      LEFT JOIN public.reefer_power_requests rpr ON rps.request_id = rpr.id
      WHERE 1=1
    `;
    const params: any[] = [];

    if (filters?.usage_status) {
      params.push(filters.usage_status);
      sql += ` AND rp.usage_status = $${params.length}`;
    }

    if (filters?.technical_status) {
      params.push(filters.technical_status);
      sql += ` AND rp.technical_status = $${params.length}`;
    }

    if (filters?.search) {
      params.push(`%${filters.search}%`);
      sql += ` AND (rp.plug_code ILIKE $${params.length} OR yb.code ILIKE $${params.length} OR c.container_no ILIKE $${params.length})`;
    }

    sql += ` ORDER BY rp.plug_code ASC`;

    const res = await query(sql, params);
    return res.rows.map(row => ({
      id: row.id,
      slot_id: row.slot_id,
      plug_code: row.plug_code,
      is_available: row.is_available,
      is_power_on: row.is_power_on,
      usage_status: row.usage_status,
      technical_status: row.technical_status,
      voltage_type: row.voltage_type,
      max_power_kw: row.max_power_kw ? parseFloat(row.max_power_kw) : 15.0,
      last_maintained_at: row.last_maintained_at,
      slot_code: row.slot_code,
      block_code: row.block_code,
      active_session: row.active_session_id ? {
        id: row.active_session_id,
        container_id: row.active_container_id,
        container_no: row.active_container_no,
        booking_code: row.active_booking_code,
        required_power_kw: row.active_required_power_kw != null ? parseFloat(row.active_required_power_kw) : (row.max_power_kw ? parseFloat(row.max_power_kw) : 15.0),
        plug_id: row.id,
        session_status: row.active_session_status,
        actual_power_status: row.active_power_status || (row.usage_status === 'OCCUPIED' ? 'POWERED_ON' : 'UNKNOWN'),
        target_temperature: row.target_temperature != null ? parseFloat(row.target_temperature) : null,
        temp_tolerance: row.temp_tolerance != null ? parseFloat(row.temp_tolerance) : 2.0,
        started_at: row.active_started_at,
      } as any : null,
      created_at: row.created_at,
      updated_at: row.updated_at,
    }));
  }

  /**
   * Chi tiết ổ cắm
   */
  async getPlugById(id: string): Promise<ReeferPlug | null> {
    const res = await query(`
      SELECT 
        rp.*,
        CASE WHEN ys.id IS NOT NULL THEN CONCAT(COALESCE(yb.code, 'R'), '-', LPAD(ys.bay::text, 2, '0'), '-', ys.row_no, '-', ys.tier) ELSE NULL END AS slot_code,
        yb.code AS block_code
      FROM public.reefer_plugs rp
      LEFT JOIN public.yard_slots ys ON rp.slot_id = ys.id
      LEFT JOIN public.yard_blocks yb ON ys.block_id = yb.id
      WHERE rp.id = $1
    `, [id]);
    if (res.rows.length === 0) return null;
    return res.rows[0];
  }

  /**
   * Cập nhật trạng thái kỹ thuật ổ cắm (Bảo trì, hư hỏng, phục hồi)
   */
  async updatePlugTechnicalStatus(
    id: string,
    technical_status: SocketTechnicalStatus,
    client?: PoolClient
  ): Promise<ReeferPlug> {
    const q = client ? client.query.bind(client) : query;
    const res = await q(`
      UPDATE public.reefer_plugs
      SET 
        technical_status = $1,
        is_available = CASE WHEN $1 = 'HEALTHY' AND usage_status = 'AVAILABLE' THEN TRUE ELSE FALSE END,
        updated_at = CURRENT_TIMESTAMP
      WHERE id = $2
      RETURNING *;
    `, [technical_status, id]);
    return res.rows[0];
  }

  /**
   * Thêm máy / ổ cấp điện kho lạnh mới
   */
  async createPlug(data: {
    plug_code: string;
    slot_id?: string | null;
    voltage_type?: string;
    max_power_kw?: number;
  }): Promise<ReeferPlug> {
    const res = await query(`
      INSERT INTO public.reefer_plugs (
        plug_code,
        slot_id,
        is_available,
        is_power_on,
        usage_status,
        technical_status,
        voltage_type,
        max_power_kw,
        created_at,
        updated_at
      ) VALUES ($1, $2, TRUE, FALSE, 'AVAILABLE', 'HEALTHY', $3, $4, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
      RETURNING *;
    `, [
      data.plug_code,
      data.slot_id || null,
      data.voltage_type || '380V-3Ph-32A',
      data.max_power_kw || 15.00
    ]);
    return res.rows[0];
  }

  /**
   * Bật / Tắt trực tiếp nguồn điện máy cấp điện kho lạnh
   */
  async togglePlugPower(id: string, is_power_on: boolean): Promise<ReeferPlug> {
    const res = await query(`
      UPDATE public.reefer_plugs
      SET 
        is_power_on = $1,
        updated_at = CURRENT_TIMESTAMP
      WHERE id = $2
      RETURNING *;
    `, [is_power_on, id]);

    // Đồng bộ session đang active nếu có
    if (res.rows.length > 0) {
      await query(`
        UPDATE public.reefer_power_sessions
        SET 
          actual_power_status = $1,
          updated_at = CURRENT_TIMESTAMP
        WHERE plug_id = $2 AND session_status = 'ACTIVE';
      `, [is_power_on ? 'POWERED_ON' : 'POWERED_OFF', id]);
    }
    return res.rows[0];
  }

  /**
   * Danh sách yêu cầu cấp điện
   */
  async getRequests(filters?: {
    status?: ReeferRequestStatus;
    container_id?: string;
    search?: string;
  }): Promise<ReeferPowerRequest[]> {
    let sql = `
      SELECT 
        rpr.*,
        c.container_no AS container_no,
        u.username AS requester_name
      FROM public.reefer_power_requests rpr
      JOIN public.containers c ON rpr.container_id = c.id
      LEFT JOIN public.users u ON rpr.requested_by = u.id
      WHERE 1=1
    `;
    const params: any[] = [];

    if (filters?.status) {
      params.push(filters.status);
      sql += ` AND rpr.status = $${params.length}`;
    }

    if (filters?.container_id) {
      params.push(filters.container_id);
      sql += ` AND rpr.container_id = $${params.length}`;
    }

    if (filters?.search) {
      params.push(`%${filters.search}%`);
      sql += ` AND (rpr.request_no ILIKE $${params.length} OR c.container_no ILIKE $${params.length})`;
    }

    sql += ` ORDER BY rpr.created_at DESC`;

    const res = await query(sql, params);
    return res.rows;
  }

  /**
   * Chi tiết yêu cầu cấp điện
   */
  async getRequestById(id: string): Promise<ReeferPowerRequest | null> {
    const res = await query(`
      SELECT 
        rpr.*,
        c.container_no AS container_no,
        u.username AS requester_name
      FROM public.reefer_power_requests rpr
      JOIN public.containers c ON rpr.container_id = c.id
      LEFT JOIN public.users u ON rpr.requested_by = u.id
      WHERE rpr.id = $1
    `, [id]);
    return res.rows[0] || null;
  }

  /**
   * Tạo yêu cầu cấp điện mới
   */
  async createRequest(data: {
    container_id: string;
    target_temperature: number;
    temperature_unit?: 'C' | 'F';
    temp_tolerance?: number;
    cargo_description?: string;
    special_instructions?: string;
    requested_by?: string;
    booking_id?: string;
    booking_code?: string;
  }): Promise<ReeferPowerRequest> {
    // Validate chỉ container lạnh mới được tạo yêu cầu cấp điện
    const contCheck = await query(`
      SELECT c.id, c.container_no, c.is_reefer, c.cargo_type, ct.category, ct.code AS type_code
      FROM public.containers c
      LEFT JOIN public.container_types ct ON c.container_type_id = ct.id
      WHERE c.id = $1;
    `, [data.container_id]);

    if (contCheck.rows.length === 0) {
      throw new Error('Container không tồn tại trong hệ thống.');
    }
    const cont = contCheck.rows[0];
    const isReefer = cont.is_reefer === true 
      || cont.category === 'reefer' 
      || cont.cargo_type === 'perishable' 
      || /RF|RE|RH|HR/i.test(cont.type_code || '');

    if (!isReefer) {
      throw new Error(`Container ${cont.container_no} không phải container lạnh (${cont.type_code || 'Khô'}), không thể tạo yêu cầu cấp điện.`);
    }

    const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
    const rand = Math.floor(1000 + Math.random() * 9000);
    const requestNo = `RPR-${dateStr}-${rand}`;

    const res = await query(`
      INSERT INTO public.reefer_power_requests (
        request_no,
        container_id,
        target_temperature,
        temperature_unit,
        temp_tolerance,
        cargo_description,
        special_instructions,
        status,
        requested_by,
        booking_id,
        booking_code
      ) VALUES ($1, $2, $3, $4, $5, $6, $7, 'PENDING_ASSIGNMENT', $8, $9, $10)
      RETURNING *;
    `, [
      requestNo,
      data.container_id,
      data.target_temperature,
      data.temperature_unit || 'C',
      data.temp_tolerance ?? 2.0,
      data.cargo_description || null,
      data.special_instructions || null,
      data.requested_by || null,
      data.booking_id || null,
      data.booking_code || null
    ]);
    return res.rows[0];
  }

  /**
   * Cập nhật trạng thái yêu cầu
   */
  async updateRequestStatus(
    id: string,
    status: ReeferRequestStatus,
    cancelledReason?: string,
    client?: PoolClient
  ): Promise<ReeferPowerRequest> {
    const q = client ? client.query.bind(client) : query;
    const res = await q(`
      UPDATE public.reefer_power_requests
      SET 
        status = $1,
        cancelled_reason = COALESCE($2, cancelled_reason),
        updated_at = CURRENT_TIMESTAMP
      WHERE id = $3
      RETURNING *;
    `, [status, cancelledReason || null, id]);
    return res.rows[0];
  }

  /**
   * Danh sách phiên cấp điện Reefer Power Sessions
   */
  async getSessions(filters?: {
    session_status?: PowerSessionStatus;
    container_id?: string;
    plug_id?: string;
    search?: string;
  }): Promise<ReeferPowerSession[]> {
    let sql = `
      SELECT 
        rps.*,
        c.container_no AS container_no,
        rp.plug_code,
        rp.slot_id,
        CASE WHEN ys.id IS NOT NULL THEN CONCAT(COALESCE(yb.code, 'R'), '-', LPAD(ys.bay::text, 2, '0'), '-', ys.row_no, '-', ys.tier) ELSE NULL END AS slot_code,
        yb.code AS block_code,
        rpr.target_temperature,
        rpr.temp_tolerance,
        u1.username AS assigned_by_name,
        u2.username AS power_confirmed_by_name,
        (
          SELECT return_temperature 
          FROM public.reefer_temperature_records rtr 
          WHERE rtr.session_id = rps.id 
          ORDER BY recorded_at DESC LIMIT 1
        ) AS last_temperature
      FROM public.reefer_power_sessions rps
      JOIN public.containers c ON rps.container_id = c.id
      JOIN public.reefer_plugs rp ON rps.plug_id = rp.id
      LEFT JOIN public.yard_slots ys ON rp.slot_id = ys.id
      LEFT JOIN public.yard_blocks yb ON ys.block_id = yb.id
      LEFT JOIN public.reefer_power_requests rpr ON rps.request_id = rpr.id
      LEFT JOIN public.users u1 ON rps.assigned_by = u1.id
      LEFT JOIN public.users u2 ON rps.power_confirmed_by = u2.id
      WHERE 1=1
    `;
    const params: any[] = [];

    if (filters?.session_status) {
      params.push(filters.session_status);
      sql += ` AND rps.session_status = $${params.length}`;
    }

    if (filters?.container_id) {
      params.push(filters.container_id);
      sql += ` AND rps.container_id = $${params.length}`;
    }

    if (filters?.plug_id) {
      params.push(filters.plug_id);
      sql += ` AND rps.plug_id = $${params.length}`;
    }

    if (filters?.search) {
      params.push(`%${filters.search}%`);
      sql += ` AND (c.container_no ILIKE $${params.length} OR rp.plug_code ILIKE $${params.length})`;
    }

    sql += ` ORDER BY rps.created_at DESC`;

    const res = await query(sql, params);
    return res.rows;
  }

  /**
   * Chi tiết phiên cấp điện
   */
  async getSessionById(id: string): Promise<ReeferPowerSession | null> {
    const res = await query(`
      SELECT 
        rps.*,
        c.container_no AS container_no,
        rp.plug_code,
        rp.slot_id,
        CASE WHEN ys.id IS NOT NULL THEN CONCAT(COALESCE(yb.code, 'R'), '-', LPAD(ys.bay::text, 2, '0'), '-', ys.row_no, '-', ys.tier) ELSE NULL END AS slot_code,
        yb.code AS block_code,
        rpr.target_temperature,
        rpr.temp_tolerance,
        u1.username AS assigned_by_name,
        u2.username AS power_confirmed_by_name,
        (
          SELECT return_temperature 
          FROM public.reefer_temperature_records rtr 
          WHERE rtr.session_id = rps.id 
          ORDER BY recorded_at DESC LIMIT 1
        ) AS last_temperature
      FROM public.reefer_power_sessions rps
      JOIN public.containers c ON rps.container_id = c.id
      JOIN public.reefer_plugs rp ON rps.plug_id = rp.id
      LEFT JOIN public.yard_slots ys ON rp.slot_id = ys.id
      LEFT JOIN public.yard_blocks yb ON ys.block_id = yb.id
      LEFT JOIN public.reefer_power_requests rpr ON rps.request_id = rpr.id
      LEFT JOIN public.users u1 ON rps.assigned_by = u1.id
      LEFT JOIN public.users u2 ON rps.power_confirmed_by = u2.id
      WHERE rps.id = $1
    `, [id]);
    return res.rows[0] || null;
  }

  /**
   * BƯỚC 1: Gán ổ cắm cho Container (Dispatcher / Điều độ bãi)
   * Tạo session với trạng thái AWAITING_POWER, khóa ổ cắm với RESERVED
   */
  async assignPlugToContainer(data: {
    container_id: string;
    plug_id: string;
    request_id?: string;
    target_temperature?: number;
    temp_tolerance?: number;
    cargo_description?: string;
    assigned_by: string;
    note?: string;
    booking_id?: string;
    booking_code?: string;
  }): Promise<ReeferPowerSession> {
    const client = await pool.connect();
    try {
      await client.query('BEGIN');

      // 1. Kiểm tra ổ cắm có sẵn sàng và healthy không (FOR UPDATE chống race condition)
      const plugRes = await client.query(`
        SELECT id, plug_code, usage_status, technical_status 
        FROM public.reefer_plugs 
        WHERE id = $1 FOR UPDATE;
      `, [data.plug_id]);

      if (plugRes.rows.length === 0) {
        throw new Error('Ổ cắm điện không tồn tại.');
      }
      const plug = plugRes.rows[0];
      if (plug.technical_status !== 'HEALTHY') {
        throw new Error(`Ổ cắm ${plug.plug_code} đang bị hỏng hoặc bảo trì (${plug.technical_status}).`);
      }
      if (plug.usage_status !== 'AVAILABLE') {
        throw new Error(`Ổ cắm ${plug.plug_code} đã có container sử dụng hoặc đã được giữ chỗ.`);
      }

      // 2. Validate chỉ cho phép cấp điện cho CONTAINER LẠNH (Reefer Container)
      const containerCheck = await client.query(`
        SELECT c.id, c.container_no, c.is_reefer, c.cargo_type, ct.category, ct.code AS type_code
        FROM public.containers c
        LEFT JOIN public.container_types ct ON c.container_type_id = ct.id
        WHERE c.id = $1;
      `, [data.container_id]);

      if (containerCheck.rows.length === 0) {
        throw new Error('Container không tồn tại trong hệ thống.');
      }
      const cont = containerCheck.rows[0];
      const isReefer = cont.is_reefer === true 
        || cont.category === 'reefer' 
        || cont.cargo_type === 'perishable' 
        || /RF|RE|RH|HR/i.test(cont.type_code || '');

      if (!isReefer) {
        throw new Error(`Container ${cont.container_no} là container hàng khô (${cont.type_code || 'Dry'}), không thể cấp điện container lạnh.`);
      }

      // 3. Kiểm tra container đã có phiên cấp điện nào đang chạy / chờ không
      const activeSessionRes = await client.query(`
        SELECT id FROM public.reefer_power_sessions 
        WHERE container_id = $1 AND session_status IN ('AWAITING_POWER', 'ACTIVE')
        LIMIT 1;
      `, [data.container_id]);

      if (activeSessionRes.rows.length > 0) {
        throw new Error('Container này hiện đã có phiên cấp điện đang hoạt động hoặc đang chờ cắm điện.');
      }

      // 3. Khóa ổ cắm: chuyển usage_status = RESERVED, is_available = FALSE
      await client.query(`
        UPDATE public.reefer_plugs
        SET usage_status = 'RESERVED', is_available = FALSE, updated_at = CURRENT_TIMESTAMP
        WHERE id = $1;
      `, [data.plug_id]);

      // 4. Đồng bộ hoặc tạo yêu cầu cấp điện nếu chưa có
      let finalRequestId = data.request_id;
      if (!finalRequestId) {
        const existingReq = await client.query(`
          SELECT id FROM public.reefer_power_requests
          WHERE container_id = $1 AND status = 'PENDING_ASSIGNMENT'
          ORDER BY created_at DESC LIMIT 1;
        `, [data.container_id]);

        if (existingReq.rows.length > 0) {
          finalRequestId = existingReq.rows[0].id;
        } else {
          const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
          const rand = Math.floor(1000 + Math.random() * 9000);
          const reqNo = `RPR-${dateStr}-${rand}`;
          const newReqRes = await client.query(`
            INSERT INTO public.reefer_power_requests (
              request_no,
              container_id,
              target_temperature,
              temperature_unit,
              temp_tolerance,
              cargo_description,
              status,
              requested_by
            ) VALUES ($1, $2, $3, 'C', $4, $5, 'ASSIGNED', $6)
            RETURNING id;
          `, [
            reqNo,
            data.container_id,
            data.target_temperature != null ? data.target_temperature : -18.0,
            data.temp_tolerance != null ? data.temp_tolerance : 2.0,
            data.cargo_description || 'Hàng lạnh tại bãi',
            data.assigned_by
          ]);
          finalRequestId = newReqRes.rows[0].id;
        }
      }

      // 5. Cập nhật request sang ASSIGNED
      if (finalRequestId) {
        await client.query(`
          UPDATE public.reefer_power_requests
          SET status = 'ASSIGNED', updated_at = CURRENT_TIMESTAMP
          WHERE id = $1;
        `, [finalRequestId]);
      }

      // 6. Tạo session ở trạng thái AWAITING_POWER (started_at = NULL)
      const sessionRes = await client.query(`
        INSERT INTO public.reefer_power_sessions (
          container_id,
          plug_id,
          request_id,
          assigned_by,
          assigned_at,
          started_at,
          session_status,
          actual_power_status,
          is_active,
          note,
          booking_id,
          booking_code
        ) VALUES ($1, $2, $3, $4, CURRENT_TIMESTAMP, NULL, 'AWAITING_POWER', 'UNKNOWN', TRUE, $5, $6, $7)
        RETURNING *;
      `, [
        data.container_id,
        data.plug_id,
        finalRequestId || null,
        data.assigned_by,
        data.note || 'Đã phân bổ ổ cắm, chờ kỹ thuật viên cắm cáp và đóng điện',
        data.booking_id || null,
        data.booking_code || null
      ]);

      await client.query('COMMIT');
      return sessionRes.rows[0];
    } catch (err) {
      await client.query('ROLLBACK');
      throw err;
    } finally {
      client.release();
    }
  }

  /**
   * BƯỚC 2: Xác nhận cấp điện thực tế (Kỹ thuật viên điện lạnh / Field Operator)
   * Chuyển session sang ACTIVE, started_at = CURRENT_TIMESTAMP, plug.usage_status = OCCUPIED
   */
  async confirmPowerOn(sessionId: string, confirmedBy: string, initialReturnTemp?: number): Promise<ReeferPowerSession> {
    const client = await pool.connect();
    try {
      await client.query('BEGIN');

      const sessionRes = await client.query(`
        SELECT * FROM public.reefer_power_sessions 
        WHERE id = $1 FOR UPDATE;
      `, [sessionId]);

      if (sessionRes.rows.length === 0) {
        throw new Error('Không tìm thấy phiên cấp điện.');
      }
      const session = sessionRes.rows[0];

      if (session.session_status !== 'AWAITING_POWER') {
        throw new Error(`Phiên cấp điện đang ở trạng thái ${session.session_status}, không thể thực hiện xác nhận cấp điện.`);
      }

      // Cập nhật session sang ACTIVE
      const updatedSessionRes = await client.query(`
        UPDATE public.reefer_power_sessions
        SET 
          session_status = 'ACTIVE',
          actual_power_status = 'POWERED_ON',
          started_at = CURRENT_TIMESTAMP,
          power_confirmed_by = $1,
          last_checked_at = CURRENT_TIMESTAMP,
          updated_at = CURRENT_TIMESTAMP
        WHERE id = $2
        RETURNING *;
      `, [confirmedBy, sessionId]);

      // Cập nhật ổ cắm sang OCCUPIED, is_power_on = TRUE
      await client.query(`
        UPDATE public.reefer_plugs
        SET 
          usage_status = 'OCCUPIED',
          is_power_on = TRUE,
          is_available = FALSE,
          updated_at = CURRENT_TIMESTAMP
        WHERE id = $1;
      `, [session.plug_id]);

      // Cập nhật request sang IN_PROGRESS nếu có
      if (session.request_id) {
        await client.query(`
          UPDATE public.reefer_power_requests
          SET status = 'IN_PROGRESS', updated_at = CURRENT_TIMESTAMP
          WHERE id = $1;
        `, [session.request_id]);
      }

      // Ghi nhận nhiệt độ ban đầu nếu kỹ thuật viên nhập
      if (initialReturnTemp !== undefined && initialReturnTemp !== null) {
        await client.query(`
          INSERT INTO public.reefer_temperature_records (
            session_id,
            return_temperature,
            temperature_unit,
            data_source,
            recorded_by,
            notes,
            recorded_at
          ) VALUES ($1, $2, 'C', 'MANUAL_INSPECTION', $3, 'Ghi nhận nhiệt độ ban đầu khi đóng điện', CURRENT_TIMESTAMP);
        `, [sessionId, initialReturnTemp, confirmedBy]);
      }

      await client.query('COMMIT');
      return updatedSessionRes.rows[0];
    } catch (err) {
      await client.query('ROLLBACK');
      throw err;
    } finally {
      client.release();
    }
  }

  /**
   * Ngắt điện & Kết thúc phiên cấp điện (Trước khi bốc xếp / dỡ khỏi bãi)
   */
  async terminateSession(sessionId: string, reason: string): Promise<ReeferPowerSession> {
    const client = await pool.connect();
    try {
      await client.query('BEGIN');

      const sessionRes = await client.query(`
        SELECT * FROM public.reefer_power_sessions 
        WHERE id = $1 FOR UPDATE;
      `, [sessionId]);

      if (sessionRes.rows.length === 0) {
        throw new Error('Không tìm thấy phiên cấp điện.');
      }
      const session = sessionRes.rows[0];

      if (['COMPLETED', 'CANCELLED'].includes(session.session_status)) {
        throw new Error('Phiên cấp điện đã kết thúc trước đó.');
      }

      // Tính giờ cấp điện thực tế (billable_hours)
      const now = new Date();
      let billableHours = 0;
      if (session.started_at) {
        const diffMs = now.getTime() - new Date(session.started_at).getTime();
        billableHours = parseFloat((diffMs / (1000 * 60 * 60)).toFixed(2));
      }

      // Đóng session
      const updatedSessionRes = await client.query(`
        UPDATE public.reefer_power_sessions
        SET 
          session_status = 'COMPLETED',
          actual_power_status = 'POWERED_OFF',
          is_active = FALSE,
          ended_at = CURRENT_TIMESTAMP,
          billable_hours = $1,
          termination_reason = $2,
          updated_at = CURRENT_TIMESTAMP
        WHERE id = $3
        RETURNING *;
      `, [billableHours, reason, sessionId]);

      // Trả lại ổ cắm về AVAILABLE, is_power_on = FALSE, is_available = TRUE
      await client.query(`
        UPDATE public.reefer_plugs
        SET 
          usage_status = 'AVAILABLE',
          is_power_on = FALSE,
          is_available = CASE WHEN technical_status = 'HEALTHY' THEN TRUE ELSE FALSE END,
          updated_at = CURRENT_TIMESTAMP
        WHERE id = $1;
      `, [session.plug_id]);

      // Cập nhật request sang COMPLETED
      if (session.request_id) {
        await client.query(`
          UPDATE public.reefer_power_requests
          SET status = 'COMPLETED', updated_at = CURRENT_TIMESTAMP
          WHERE id = $1;
        `, [session.request_id]);
      }

      await client.query('COMMIT');
      return updatedSessionRes.rows[0];
    } catch (err) {
      await client.query('ROLLBACK');
      throw err;
    } finally {
      client.release();
    }
  }

  /**
   * Chuyển ổ cắm (Trong trường hợp ổ cắm hiện tại bị sự cố kỹ thuật hoặc di dời container)
   */
  async transferPlug(sessionId: string, newPlugId: string, reason: string, userId: string): Promise<ReeferPowerSession> {
    const client = await pool.connect();
    try {
      await client.query('BEGIN');

      const sessionRes = await client.query(`
        SELECT * FROM public.reefer_power_sessions WHERE id = $1 FOR UPDATE;
      `, [sessionId]);
      if (sessionRes.rows.length === 0) throw new Error('Không tìm thấy phiên cấp điện.');
      const oldSession = sessionRes.rows[0];

      // Khóa và kiểm tra ổ cắm mới
      const newPlugRes = await client.query(`
        SELECT * FROM public.reefer_plugs WHERE id = $1 FOR UPDATE;
      `, [newPlugId]);
      if (newPlugRes.rows.length === 0) throw new Error('Ổ cắm mới không tồn tại.');
      const newPlug = newPlugRes.rows[0];
      if (newPlug.usage_status !== 'AVAILABLE' || newPlug.technical_status !== 'HEALTHY') {
        throw new Error(`Ổ cắm đích ${newPlug.plug_code} không sẵn sàng hoặc đang lỗi.`);
      }

      // Đóng phiên cũ với trạng thái TRANSFERRED
      const now = new Date();
      let billableHours = 0;
      if (oldSession.started_at) {
        billableHours = parseFloat(((now.getTime() - new Date(oldSession.started_at).getTime()) / (1000 * 60 * 60)).toFixed(2));
      }

      await client.query(`
        UPDATE public.reefer_power_sessions
        SET 
          session_status = 'TRANSFERRED',
          actual_power_status = 'POWERED_OFF',
          is_active = FALSE,
          ended_at = CURRENT_TIMESTAMP,
          billable_hours = $1,
          termination_reason = $2,
          updated_at = CURRENT_TIMESTAMP
        WHERE id = $3;
      `, [billableHours, `Chuyển sang ổ ${newPlug.plug_code}: ${reason}`, sessionId]);

      // Trả ổ cũ về AVAILABLE hoặc giữ FAULTY
      await client.query(`
        UPDATE public.reefer_plugs
        SET 
          usage_status = 'AVAILABLE',
          is_power_on = FALSE,
          is_available = CASE WHEN technical_status = 'HEALTHY' THEN TRUE ELSE FALSE END,
          updated_at = CURRENT_TIMESTAMP
        WHERE id = $1;
      `, [oldSession.plug_id]);

      // Đặt ổ mới là OCCUPIED và tạo phiên mới kế thừa
      await client.query(`
        UPDATE public.reefer_plugs
        SET usage_status = 'OCCUPIED', is_power_on = TRUE, is_available = FALSE, updated_at = CURRENT_TIMESTAMP
        WHERE id = $1;
      `, [newPlugId]);

      const newSessionRes = await client.query(`
        INSERT INTO public.reefer_power_sessions (
          container_id,
          plug_id,
          request_id,
          assigned_by,
          power_confirmed_by,
          assigned_at,
          started_at,
          session_status,
          actual_power_status,
          is_active,
          parent_session_id,
          note
        ) VALUES ($1, $2, $3, $4, $4, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 'ACTIVE', 'POWERED_ON', TRUE, $5, $6)
        RETURNING *;
      `, [
        oldSession.container_id,
        newPlugId,
        oldSession.request_id,
        userId,
        oldSession.id,
        `Kế thừa từ phiên ${oldSession.id} do chuyển ổ cắm: ${reason}`
      ]);

      await client.query('COMMIT');
      return newSessionRes.rows[0];
    } catch (err) {
      await client.query('ROLLBACK');
      throw err;
    } finally {
      client.release();
    }
  }

  /**
   * Ghi log nhiệt độ container
   */
  async recordTemperature(data: {
    session_id: string;
    supply_temperature?: number;
    return_temperature: number;
    temperature_unit?: 'C' | 'F';
    ambient_temperature?: number;
    data_source?: TempDataSource;
    recorded_by?: string;
    device_id?: string;
    notes?: string;
  }): Promise<ReeferTemperatureRecord> {
    // 1. Kiểm tra session và ngưỡng nhiệt độ mong muốn
    const sessionRes = await query(`
      SELECT 
        rps.id, rps.container_id, rps.plug_id,
        rpr.target_temperature, rpr.temp_tolerance
      FROM public.reefer_power_sessions rps
      LEFT JOIN public.reefer_power_requests rpr ON rps.request_id = rpr.id
      WHERE rps.id = $1;
    `, [data.session_id]);

    if (sessionRes.rows.length === 0) {
      throw new Error('Phiên cấp điện không tồn tại.');
    }
    const session = sessionRes.rows[0];

    // Đánh giá bất thường (nếu lệch quá biên độ temp_tolerance)
    let isAbnormal = false;
    const targetTemp = session.target_temperature ? parseFloat(session.target_temperature) : null;
    const tolerance = session.temp_tolerance ? parseFloat(session.temp_tolerance) : 2.0;

    if (targetTemp !== null) {
      const diff = Math.abs(data.return_temperature - targetTemp);
      if (diff > tolerance) {
        isAbnormal = true;
      }
    }

    const res = await query(`
      INSERT INTO public.reefer_temperature_records (
        session_id,
        supply_temperature,
        return_temperature,
        temperature_unit,
        ambient_temperature,
        data_source,
        is_abnormal,
        recorded_by,
        device_id,
        notes,
        recorded_at
      ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, CURRENT_TIMESTAMP)
      RETURNING *;
    `, [
      data.session_id,
      data.supply_temperature ?? null,
      data.return_temperature,
      data.temperature_unit || 'C',
      data.ambient_temperature ?? null,
      data.data_source || 'MANUAL_INSPECTION',
      isAbnormal,
      data.recorded_by || null,
      data.device_id || null,
      data.notes || null
    ]);

    // Cập nhật last_checked_at trên session
    await query(`
      UPDATE public.reefer_power_sessions
      SET last_checked_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP
      WHERE id = $1;
    `, [data.session_id]);

    // Đánh giá và phát cảnh báo (chống cảnh báo ảo khi dao động nhẹ)
    if (isAbnormal && targetTemp !== null) {
      const diff = Math.abs(data.return_temperature - targetTemp);
      const isCritical = diff > 5.0; // Lệch rất lớn (> 5°C): Nguy cơ hỏng hàng cao, kích hoạt ngay

      // Kiểm tra lần đo ngay trước đó của phiên này để xác định có vượt ngưỡng liên tiếp không
      const prevRecordRes = await query(`
        SELECT is_abnormal 
        FROM public.reefer_temperature_records
        WHERE session_id = $1
        ORDER BY recorded_at DESC
        LIMIT 1 OFFSET 1;
      `, [data.session_id]);

      const wasPrevAbnormal = prevRecordRes.rows.length > 0 && prevRecordRes.rows[0].is_abnormal;

      // Chỉ phát cảnh báo sự cố nếu: Lệch nghiêm trọng (> 5°C) HOẶC vượt ngưỡng ít nhất 2 lần liên tiếp
      if (isCritical || wasPrevAbnormal) {
        const existingAlertRes = await query(`
          SELECT id FROM public.reefer_alerts
          WHERE session_id = $1 AND alert_type = 'TEMPERATURE_EXCURSION' AND status IN ('TRIGGERED', 'ACKNOWLEDGED')
          LIMIT 1;
        `, [data.session_id]);

        if (existingAlertRes.rows.length === 0) {
          const lowerSafe = (targetTemp - tolerance).toFixed(1);
          const upperSafe = (targetTemp + tolerance).toFixed(1);
          await this.createAlert({
            alert_code: `TEMP_EXCURSION_${Date.now()}`,
            session_id: data.session_id,
            plug_id: session.plug_id,
            container_id: session.container_id,
            alert_type: 'TEMPERATURE_EXCURSION',
            severity: isCritical ? 'CRITICAL' : 'WARNING',
            message: `Nhiệt độ ${data.return_temperature}°C vượt ngoài vùng an toàn [${lowerSafe}°C đến ${upperSafe}°C] liên tiếp (Cài đặt: ${targetTemp}°C ± ${tolerance}°C)`,
            payload: {
              recorded_temp: data.return_temperature,
              target_temp: targetTemp,
              tolerance,
              safe_range: [lowerSafe, upperSafe],
              consecutive_count: wasPrevAbnormal ? 2 : 1,
              is_critical: isCritical,
              recorded_at: new Date().toISOString()
            }
          });
        }
      }
    } else if (!isAbnormal && targetTemp !== null) {
      // Khi nhiệt độ đã trở về vùng an toàn: Tự động hoàn tất (RESOLVED) các cảnh báo lệch nhiệt đang mở
      await query(`
        UPDATE public.reefer_alerts
        SET status = 'RESOLVED',
            resolution_notes = 'Nhiệt độ đo được đã trở về vùng an toàn cho phép theo dữ liệu kiểm tra mới nhất.',
            updated_at = CURRENT_TIMESTAMP
        WHERE session_id = $1 
          AND alert_type = 'TEMPERATURE_EXCURSION' 
          AND status IN ('TRIGGERED', 'ACKNOWLEDGED');
      `, [data.session_id]);
    }

    return res.rows[0];
  }

  /**
   * Lịch sử nhiệt độ của một session
   */
  async getTemperatureHistory(sessionId: string): Promise<ReeferTemperatureRecord[]> {
    const res = await query(`
      SELECT 
        rtr.*,
        u.username AS recorder_name
      FROM public.reefer_temperature_records rtr
      LEFT JOIN public.users u ON rtr.recorded_by = u.id
      WHERE rtr.session_id = $1
      ORDER BY rtr.recorded_at ASC;
    `, [sessionId]);
    return res.rows;
  }

  /**
   * Danh sách Cảnh báo Reefer Alerts
   */
  async getAlerts(filters?: {
    status?: ReeferAlertStatus;
    severity?: AlertSeverity;
    session_id?: string;
  }): Promise<ReeferAlert[]> {
    let sql = `
      SELECT 
        ra.*,
        c.container_no AS container_no,
        rp.plug_code,
        u1.username AS acknowledged_by_name,
        u2.username AS resolved_by_name
      FROM public.reefer_alerts ra
      LEFT JOIN public.containers c ON ra.container_id = c.id
      LEFT JOIN public.reefer_plugs rp ON ra.plug_id = rp.id
      LEFT JOIN public.users u1 ON ra.acknowledged_by = u1.id
      LEFT JOIN public.users u2 ON ra.resolved_by = u2.id
      WHERE 1=1
    `;
    const params: any[] = [];

    if (filters?.status) {
      params.push(filters.status);
      sql += ` AND ra.status = $${params.length}`;
    }

    if (filters?.severity) {
      params.push(filters.severity);
      sql += ` AND ra.severity = $${params.length}`;
    }

    if (filters?.session_id) {
      params.push(filters.session_id);
      sql += ` AND ra.session_id = $${params.length}`;
    }

    sql += ` ORDER BY ra.created_at DESC`;

    const res = await query(sql, params);
    return res.rows;
  }

  /**
   * Tạo cảnh báo
   */
  async createAlert(data: {
    alert_code: string;
    session_id?: string;
    plug_id?: string;
    container_id?: string;
    alert_type: string;
    severity: AlertSeverity;
    message: string;
    payload?: any;
  }): Promise<ReeferAlert> {
    const res = await query(`
      INSERT INTO public.reefer_alerts (
        alert_code, session_id, plug_id, container_id,
        alert_type, severity, status, message, payload
      ) VALUES ($1, $2, $3, $4, $5, $6, 'TRIGGERED', $7, $8)
      RETURNING *;
    `, [
      data.alert_code,
      data.session_id || null,
      data.plug_id || null,
      data.container_id || null,
      data.alert_type,
      data.severity,
      data.message,
      data.payload ? JSON.stringify(data.payload) : null
    ]);
    return res.rows[0];
  }

  /**
   * Tiếp nhận cảnh báo (Acknowledge)
   */
  async acknowledgeAlert(id: string, userId: string): Promise<ReeferAlert> {
    const res = await query(`
      UPDATE public.reefer_alerts
      SET 
        status = 'ACKNOWLEDGED',
        acknowledged_by = $1,
        acknowledged_at = CURRENT_TIMESTAMP,
        updated_at = CURRENT_TIMESTAMP
      WHERE id = $2
      RETURNING *;
    `, [userId, id]);
    return res.rows[0];
  }

  /**
   * Xử lý đóng cảnh báo (Resolve)
   */
  async resolveAlert(id: string, userId: string, notes: string): Promise<ReeferAlert> {
    const res = await query(`
      UPDATE public.reefer_alerts
      SET 
        status = 'RESOLVED',
        resolved_by = $1,
        resolved_at = CURRENT_TIMESTAMP,
        resolution_notes = $2,
        updated_at = CURRENT_TIMESTAMP
      WHERE id = $3
      RETURNING *;
    `, [userId, notes, id]);
    return res.rows[0];
  }
  /**
   * Tự động kết thúc phiên cấp điện và giải phóng ổ cắm khi container xuất bãi
   */
  async syncDepartedContainers(): Promise<number> {
    const res = await query(`
      SELECT rps.id, c.container_no
      FROM public.reefer_power_sessions rps
      JOIN public.containers c ON rps.container_id = c.id
      WHERE rps.session_status IN ('AWAITING_POWER', 'ACTIVE')
        AND (c.status::text IN ('departed', 'gate_out', 'cancelled') OR c.left_at IS NOT NULL);
    `);

    let count = 0;
    for (const row of res.rows) {
      await this.terminateSession(row.id, 'Container đã xuất cảng / rời bãi (Tự động giải phóng ổ cắm)');
      count++;
    }
    return count;
  }

  /**
   * Hủy gán ổ cắm khi phiên đang ở trạng thái Chờ cắm dây (AWAITING_POWER)
   */
  async cancelReservation(sessionId: string, reason: string): Promise<ReeferPowerSession> {
    const client = await pool.connect();
    try {
      await client.query('BEGIN');

      const sessionRes = await client.query(`
        SELECT * FROM public.reefer_power_sessions WHERE id = $1 FOR UPDATE;
      `, [sessionId]);

      if (sessionRes.rows.length === 0) throw new Error('Không tìm thấy phiên cấp điện.');
      const session = sessionRes.rows[0];

      if (session.session_status !== 'AWAITING_POWER') {
        throw new Error('Chỉ có thể hủy gán đối với các phiên đang ở trạng thái Chờ cắm dây / Chờ xác nhận nguồn điện.');
      }

      // Đóng phiên với trạng thái CANCELLED
      const updatedSessionRes = await client.query(`
        UPDATE public.reefer_power_sessions
        SET 
          session_status = 'CANCELLED',
          actual_power_status = 'POWERED_OFF',
          is_active = FALSE,
          ended_at = CURRENT_TIMESTAMP,
          termination_reason = $1,
          updated_at = CURRENT_TIMESTAMP
        WHERE id = $2
        RETURNING *;
      `, [reason, sessionId]);

      // Giải phóng ổ cắm về AVAILABLE
      await client.query(`
        UPDATE public.reefer_plugs
        SET 
          usage_status = 'AVAILABLE',
          is_power_on = FALSE,
          is_available = CASE WHEN technical_status = 'HEALTHY' THEN TRUE ELSE FALSE END,
          updated_at = CURRENT_TIMESTAMP
        WHERE id = $1;
      `, [session.plug_id]);

      // Nếu có request, hoàn trả về PENDING_ASSIGNMENT
      if (session.request_id) {
        await client.query(`
          UPDATE public.reefer_power_requests
          SET status = 'PENDING_ASSIGNMENT', updated_at = CURRENT_TIMESTAMP
          WHERE id = $1;
        `, [session.request_id]);
      }

      await client.query('COMMIT');
      return updatedSessionRes.rows[0];
    } catch (err) {
      await client.query('ROLLBACK');
      throw err;
    } finally {
      client.release();
    }
  }
}

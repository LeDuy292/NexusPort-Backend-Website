import { getClient, query } from '../../../infrastructure/database/db';
import { AppError } from '../../../shared/errors/app-error';
import { ContainerIdentitySearchDto } from '../application/container.dto';

export type ContainerRole = 'Administrator' | 'Dispatcher' | 'Yard Staff' | 'Gate Officer' | 'Carrier' | 'Shipping Line' | 'Driver';

interface YardVisitReader {
  id: string;
  role: ContainerRole;
}

const ownShippingLine = `SELECT a.shipping_line_id FROM shipping_line_accounts a JOIN users u ON u.id=a.user_id
  WHERE u.id=? AND u.role='Shipping Line' AND u.is_active`;

export class ContainerJourneyRepository {
  async getYardOverview() {
    const result = await query(`
      WITH local_day AS (
        SELECT (now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS today
      ), inbound_today AS (
        SELECT count(DISTINCT v.id)::int AS total
        FROM container_visits v
        JOIN booking_containers bc ON bc.container_visit_id=v.id AND bc.container_id=v.container_id
        JOIN bookings b ON b.id=bc.booking_id
        CROSS JOIN local_day d
        WHERE v.visit_status='planned'
          AND yard_booking_direction(b.id,bc.container_id)='dropoff'
          AND b.status::text NOT IN ('rejected','canceled','expired')
          AND (b.appointment_start AT TIME ZONE 'Asia/Ho_Chi_Minh')::date=d.today
      ), outbound_today AS (
        SELECT count(DISTINCT scheduled.visit_id)::int AS total
        FROM (
          SELECT v.id AS visit_id
          FROM container_visits v
          JOIN booking_containers bc ON bc.container_visit_id=v.id AND bc.container_id=v.container_id
          JOIN bookings b ON b.id=bc.booking_id
          CROSS JOIN local_day d
          WHERE v.visit_status='active'
            AND yard_booking_direction(b.id,bc.container_id)='pickup'
            AND b.status::text NOT IN ('rejected','canceled','expired')
            AND (b.appointment_start AT TIME ZONE 'Asia/Ho_Chi_Minh')::date=d.today
          UNION
          SELECT v.id AS visit_id
          FROM container_visits v
          JOIN booking_containers bc ON bc.container_visit_id=v.id AND bc.container_id=v.container_id
          JOIN bookings gate_booking ON gate_booking.id=bc.booking_id
          JOIN export_gate_links l ON l.gate_booking_id=gate_booking.id
          JOIN export_booking_containers e ON e.id=l.export_container_id AND e.container_id=v.container_id
          JOIN export_bookings eb ON eb.id=e.export_booking_id
          JOIN export_voyages ev ON ev.id=eb.voyage_id
          CROSS JOIN local_day d
          WHERE v.visit_status='active' AND e.active AND e.status='GateIn'
            AND eb.status NOT IN ('Cancelled','Rejected')
            AND (ev.etd AT TIME ZONE 'Asia/Ho_Chi_Minh')::date=d.today
        ) scheduled
      ), overdue_visits AS (
        SELECT DISTINCT ON (v.id)
          v.id,
          (b.appointment_start AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS booking_day
        FROM container_visits v
        JOIN booking_containers bc ON bc.container_visit_id=v.id AND bc.container_id=v.container_id
        JOIN bookings b ON b.id=bc.booking_id
        WHERE v.visit_status='active'
          AND yard_booking_direction(b.id,bc.container_id)='dropoff'
          AND b.status::text NOT IN ('rejected','canceled','expired')
          AND NOT EXISTS (
            SELECT 1 FROM export_gate_links l
            JOIN export_booking_containers e ON e.id=l.export_container_id
            WHERE l.gate_booking_id=bc.booking_id AND e.container_id=v.container_id AND e.status='Loaded'
          )
        ORDER BY v.id,b.appointment_start
      )
      SELECT
        (SELECT today FROM local_day)::text AS "today",
        (SELECT total FROM inbound_today) AS "expectedInboundToday",
        (SELECT total FROM outbound_today) AS "expectedOutboundToday",
        (SELECT count(*)::int FROM container_visits v WHERE v.visit_status='active'
          AND NOT EXISTS (
            SELECT 1 FROM booking_containers bc JOIN export_gate_links l ON l.gate_booking_id=bc.booking_id
            JOIN export_booking_containers e ON e.id=l.export_container_id
            WHERE bc.container_visit_id=v.id AND e.container_id=v.container_id AND e.status='Loaded'
          )) AS "yardInventory",
        (SELECT count(*)::int FROM overdue_visits,local_day WHERE booking_day <= today - 5) AS "overdue"
    `);
    return result.rows[0];
  }

  async lookup(number: string) {
    if (!/^[A-Z]{4}\d{7}$/.test(number)) throw new AppError('Nhập đầy đủ số Container gồm 11 ký tự.', 422);
    const result = await query(`SELECT c.id,c.container_no AS "containerNumber",ct.id AS "containerTypeId",
      ct.code AS "containerTypeCode",ct.description AS "containerTypeDescription"
      FROM containers c JOIN container_types ct ON ct.id=c.container_type_id WHERE c.container_no=$1 AND NOT c.identity_quarantined`, [number]);
    return result.rows[0] ?? null;
  }

  listOwnIdentities(userId: string, filters: Omit<ContainerIdentitySearchDto, 'shippingLineId'>) {
    return this.searchIdentities(filters, userId);
  }

  async searchIdentities(filters: ContainerIdentitySearchDto, shippingLineUserId?: string) {
    const { containerNumber, prefix, shippingLineId, containerTypeId, isoSizeType, size, category,
      manufacturer, minPayloadKg, page, limit } = filters;
    const params: Array<string | number> = [];
    const conditions: string[] = ['NOT c.identity_quarantined'];
    const add = (condition: string, value?: string | number) => {
      if (value === undefined) return;
      params.push(value);
      conditions.push(condition.replace('?', '$' + params.length));
    };
    add('c.container_no LIKE ?', containerNumber && containerNumber + '%');
    add('c.container_no LIKE ?', prefix && prefix + '%');
    add('l.id = ?', shippingLineId);
    add('c.container_type_id = ?', containerTypeId);
    add('c.iso_size_type = ?', isoSizeType);
    add('ct.size = ?', size);
    add('ct.category = ?', category);
    add('strpos(lower(c.manufacturer),lower(?)) > 0', manufacturer);
    add('c.max_gross_mass_kg-c.tare_weight_kg >= ?', minPayloadKg);
    add(`l.id = (${ownShippingLine})`, shippingLineUserId);
    if (conditions.length === 1) throw new AppError('Nhập số container hoặc chọn ít nhất một bộ lọc.', 422);
    const from = `FROM containers c JOIN container_types ct ON ct.id=c.container_type_id
      LEFT JOIN shipping_line_prefixes p ON c.shipping_line_id IS NULL AND p.prefix=left(c.container_no,4)
      LEFT JOIN shipping_lines l ON l.id=COALESCE(c.shipping_line_id,p.shipping_line_id)
      WHERE ${conditions.join(' AND ')}`;
    const total = Number((await query('SELECT count(*) ' + from, params)).rows[0].count);
    const { rows } = await query(`SELECT c.id,c.container_no AS "containerNumber",ct.code AS "containerTypeCode",
      ct.description AS "containerTypeDescription",ct.size,ct.category,
      l.id AS "shippingLineId",l.full_name AS "shippingLineFullName",l.short_name AS "shippingLineShortName",
      left(c.container_no,4) AS prefix,c.iso_size_type AS "isoSizeType",c.equipment_description AS "equipmentDescription",
      c.external_length_mm AS "externalLengthMm",c.external_width_mm AS "externalWidthMm",c.external_height_mm AS "externalHeightMm",
      c.tare_weight_kg::float8 AS "tareWeightKg",c.max_gross_mass_kg::float8 AS "maxGrossMassKg",
      (c.max_gross_mass_kg-c.tare_weight_kg)::float8 AS "maxPayloadKg",
      c.capacity_m3::float8 AS "capacityM3",c.manufacturer
      ${from} ORDER BY c.container_no LIMIT $${params.length + 1} OFFSET $${params.length + 2}`,
      [...params, limit, (page - 1) * limit]);
    return { items: rows, total, page, limit, totalPages: Math.ceil(total / limit) };
  }

  async search(filters: Record<string, unknown>, identityOnly = false) {
    if (!identityOnly) return this.searchVisits(filters);
    const number = String(filters.containerNumber ?? '').trim().toUpperCase();
    const plate = String(filters.plateNumber ?? '').replace(/[^a-z0-9]/gi, '').toUpperCase();
    const customer = String(filters.customerName ?? '').trim();
    if (plate || customer || filters.dateFrom || filters.dateTo)
      throw new AppError('Công ty vận tải chỉ được tìm theo số Container.', 403);
    if (!/^[A-Z0-9]{4,11}$/.test(number))
      throw new AppError('Nhập số Container từ 4 đến 11 ký tự để tìm kiếm.', 422);
    const page = Math.max(1, Math.min(Math.trunc(Number(filters.page)) || 1, 10000));
    const limit = Math.max(1, Math.min(Math.trunc(Number(filters.limit)) || 20, 50));
    return this.searchIdentities({ containerNumber: number, page, limit });
  }

  searchYardVisits(filters: Record<string, unknown>, reader: YardVisitReader) {
    return this.searchVisits(filters, reader);
  }

  private async searchVisits(filters: Record<string, unknown>, reader?: YardVisitReader) {
    const number = String(filters.containerNumber ?? '').trim().toUpperCase();
    const plate = String(filters.plateNumber ?? '').replace(/[^a-z0-9]/gi, '').toUpperCase();
    const customer = String(filters.customerName ?? '').trim();
    const status = reader ? String(filters.visitStatus ?? '').trim() : '';
    const sort = String(filters.sort ?? '').trim();
    const from = filters.dateFrom ? new Date(String(filters.dateFrom)) : null;
    const to = filters.dateTo ? new Date(String(filters.dateTo)) : null;
    if ((from && !Number.isFinite(from.getTime())) || (to && !Number.isFinite(to.getTime())) || (from && to && from > to))
      throw new AppError('Khoảng thời gian không hợp lệ.', 422);
    const canReadPrivate = !reader || ['Administrator', 'Dispatcher', 'Yard Staff', 'Gate Officer'].includes(reader.role);
    const loadedOnVessel = `EXISTS(SELECT 1 FROM booking_containers vessel_bc
      JOIN export_gate_links vessel_link ON vessel_link.gate_booking_id=vessel_bc.booking_id
      JOIN export_booking_containers vessel_container ON vessel_container.id=vessel_link.export_container_id
      WHERE vessel_bc.container_visit_id=v.id AND vessel_bc.container_id=v.container_id
        AND vessel_container.container_id=v.container_id AND vessel_container.status='Loaded')`;
    const vesselLoadedAt = `(SELECT max(vessel_container.loaded_at) FROM booking_containers vessel_bc
      JOIN export_gate_links vessel_link ON vessel_link.gate_booking_id=vessel_bc.booking_id
      JOIN export_booking_containers vessel_container ON vessel_container.id=vessel_link.export_container_id
      WHERE vessel_bc.container_visit_id=v.id AND vessel_bc.container_id=v.container_id
        AND vessel_container.container_id=v.container_id AND vessel_container.status='Loaded')`;
    const recentActivity = sort === 'recentActivity' && !!reader && canReadPrivate;
    if (sort && sort !== 'recentActivity') throw new AppError('Kiểu sắp xếp lượt bãi không hợp lệ.', 422);
    if (sort === 'recentActivity' && !recentActivity) throw new AppError('Vai trò này không được xem danh sách hoạt động bãi.', 403);
    if (!canReadPrivate && (plate || customer)) throw new AppError('Vai trò này không được lọc theo xe hoặc khách hàng.', 403);
    if (!number && !plate && !customer && !(reader && (status || from || to)) && !recentActivity)
      throw new AppError('Nhập số Container hoặc chọn ít nhất một bộ lọc.', 422);
    if (number && !/^[A-Z0-9]{4,11}$/.test(number)) throw new AppError('Số Container tìm kiếm không hợp lệ.', 422);
    if (plate && plate.length < 4) throw new AppError('Biển số xe cần ít nhất 4 ký tự.', 422);
    if (customer && customer.length < 3 && (reader || (!number && !plate)))
      throw new AppError('Tên khách hàng cần ít nhất 3 ký tự.', 422);
    if (status && !['planned', 'active', 'completed', 'canceled'].includes(status))
      throw new AppError('Trạng thái lượt bãi không hợp lệ.', 422);
    const page = Math.max(1, Math.min(Math.trunc(Number(filters.page)) || 1, 10000));
    const limit = Math.max(1, Math.min(Math.trunc(Number(filters.limit)) || 20, 50));
    const params: unknown[] = [];
    const where: string[] = [];
    if (recentActivity && !number && !plate && !customer && !status && !from && !to) where.push('TRUE');
    const bind = (value: unknown) => '$' + params.push(value);
    const add = (sql: string, value: unknown) => where.push(sql.replace(/\?/g, bind(value)));
    const bookingsForVisit = 'FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id';
    let bookingScope = '';
    if (reader?.role === 'Carrier') {
      const actor = bind(reader.id);
      const carrierIds = `SELECT carrier_id FROM carrier_users WHERE user_id=${actor}`;
      bookingScope = ` AND b.carrier_id IN (${carrierIds})`;
      where.push(`(EXISTS(SELECT 1 ${bookingsForVisit} WHERE bc.container_visit_id=v.id${bookingScope})
        OR EXISTS(SELECT 1 FROM container_visit_access a WHERE a.container_visit_id=v.id AND a.carrier_id IN (${carrierIds})))`);
    } else if (reader?.role === 'Driver') {
      bookingScope = ` AND b.driver_id IN (SELECT id FROM drivers WHERE user_id=${bind(reader.id)})`;
      where.push(`EXISTS(SELECT 1 ${bookingsForVisit} WHERE bc.container_visit_id=v.id${bookingScope})`);
    } else if (reader?.role === 'Shipping Line') {
      add(`COALESCE(c.shipping_line_id,(SELECT p.shipping_line_id FROM shipping_line_prefixes p WHERE p.prefix=left(c.container_no,4)))
        = (${ownShippingLine})`, reader.id);

    }
    if (number) add('c.container_no LIKE ?', number + '%');
    if (customer) add("v.customer_name ILIKE ? ESCAPE '\\'", '%' + customer.replace(/[\\%_]/g, '\\$&') + '%');
    if (plate) add(`(EXISTS(SELECT 1 FROM eir_transactions ex WHERE ex.container_visit_id=v.id AND regexp_replace(upper(ex.plate_number),'[^A-Z0-9]','','g') LIKE ?)
      OR EXISTS(SELECT 1 FROM booking_containers bx JOIN bookings b ON b.id=bx.booking_id LEFT JOIN trucks t ON t.id=b.truck_id
      WHERE bx.container_visit_id=v.id AND regexp_replace(upper(COALESCE(b.vehicle_plate,t.plate_number)),'[^A-Z0-9]','','g') LIKE ?))`, plate + '%');
    if (status === 'active') where.push(`v.visit_status='active' AND NOT ${loadedOnVessel}`);
    else if (status === 'completed') where.push(`(v.visit_status='completed' OR (v.visit_status='active' AND ${loadedOnVessel}))`);
    else if (status) add('v.visit_status=?', status);
    if (from || to) {
      const fromParam = from && bind(from);
      const toParam = to && bind(to);
      const inRange = (column: string) => [
        fromParam && `${column}>=${fromParam}`, toParam && `${column}<${toParam}`,
      ].filter(Boolean).join(' AND ');
        const completedAt = `COALESCE(v.completed_at,${vesselLoadedAt})`;
      where.push(reader
        ? `((${inRange('v.started_at')}) OR (${inRange(completedAt)}) OR EXISTS(
            SELECT 1 ${bookingsForVisit} WHERE bc.container_visit_id=v.id${bookingScope}
            AND b.status::text NOT IN ('rejected','canceled','expired') AND ${inRange('b.appointment_start')}))`
        : inRange('COALESCE(v.started_at,v.created_at)'));
    }
    const visitPrivate = (column: string) => canReadPrivate ? column : 'NULL';
    const bookingPlate = canReadPrivate || bookingScope ? 'COALESCE(b.vehicle_plate,t.plate_number)' : 'NULL';
    const bookingCode = canReadPrivate || bookingScope ? 'b.booking_code' : 'NULL';
    const direction = reader ? 'yard_booking_direction(b.id,bc.container_id)' : 'b.booking_type';
    const receiptScope = bookingScope ? ` AND EXISTS(SELECT 1 FROM bookings b WHERE b.id=e.booking_id${bookingScope})` : '';
    const issuedReceiptFilter = reader ? " AND e.eir_status='issued'" : '';
    const latestActivity = `GREATEST(COALESCE(v.started_at,v.created_at),COALESCE(v.completed_at,${vesselLoadedAt},v.created_at),
      COALESCE((SELECT MAX(COALESCE(e.check_out_at,e.check_in_at)) FROM eir_transactions e
        WHERE e.container_visit_id=v.id${issuedReceiptFilter}),v.created_at))`;
    const base = `FROM container_visits v JOIN containers c ON c.id=v.container_id JOIN container_types ct ON ct.id=c.container_type_id WHERE ${where.join(' AND ')}`;
    const total = Number((await query('SELECT count(*) ' + base,params)).rows[0].count);
    const rows = await query(`SELECT v.id,c.id AS "containerId",c.container_no AS "containerNumber",ct.code AS "containerTypeCode",
      v.visit_reference AS "visitReference",CASE WHEN v.visit_status='active' AND ${loadedOnVessel} THEN 'completed' ELSE v.visit_status::text END AS "visitStatus",
      v.started_at AS "startedAt",COALESCE(v.completed_at,${vesselLoadedAt}) AS "completedAt",
      ${visitPrivate('v.customer_name')} AS "customerName",${visitPrivate('v.seal_number')} AS "sealNumber",
      v.load_status AS "loadStatus",${visitPrivate('v.cargo_type')} AS "cargoType",
      ${visitPrivate('v.gross_weight_kg::float8')} AS "grossWeightKg",${visitPrivate('v.required_power_kw::float8')} AS "requiredPowerKw",
      (SELECT json_agg(json_build_object('id',b.id,'bookingCode',${bookingCode},'bookingType',${direction},'status',b.status,
       'plateNumber',${bookingPlate},'appointmentStart',b.appointment_start,'appointmentEnd',b.appointment_end) ORDER BY b.appointment_start,b.id)
       ${bookingsForVisit} LEFT JOIN trucks t ON t.id=b.truck_id WHERE bc.container_visit_id=v.id${bookingScope}) AS bookings,
      (SELECT json_agg(json_build_object('id',e.id,'referenceNo',e.reference_no,'activity',e.activity,'plateNumber',${visitPrivate('e.plate_number')},
       'checkInAt',e.check_in_at,'checkOutAt',e.check_out_at,'locationCode',e.location_code,'customerName',${visitPrivate('e.customer_name')},
       'cargoTypeName',${visitPrivate('e.cargo_type_description')},'grossWeightKg',${visitPrivate('e.gross_weight_kg')},
       'requiredPowerKw',${visitPrivate('e.required_power_kw')},'sealNumber',${visitPrivate("(SELECT string_agg(s.seal_number,', ') FROM eir_seals s WHERE s.eir_transaction_id=e.id)")})
       ORDER BY e.check_in_at,e.id)
       FROM eir_transactions e WHERE e.container_visit_id=v.id${reader ? " AND e.eir_status='issued'" : ''}${receiptScope}) AS receipts
      ${base} ORDER BY ${latestActivity} DESC,v.id LIMIT $${params.length+1} OFFSET $${params.length+2}`, [...params,limit,(page-1)*limit]);
    return { items: rows.rows,total,page,limit,totalPages: Math.ceil(total/limit) };
  }

  async ownVisits(containerId: string, userId: string) {
    return (await query(`SELECT DISTINCT v.id,v.visit_reference AS "visitReference",v.visit_status AS "visitStatus",v.started_at AS "startedAt"
      FROM container_visits v JOIN container_visit_access a ON a.container_visit_id=v.id JOIN carrier_users cu ON cu.carrier_id=a.carrier_id
      WHERE v.container_id=$1 AND cu.user_id=$2 AND v.visit_status IN ('planned','active') ORDER BY v.started_at DESC`,[containerId,userId])).rows;
  }

  async legacyBookings(containerNumber: string) {
    if(!/^[A-Z0-9]{4,11}$/.test(containerNumber)) return [];
    return (await query(`SELECT b.id,b.booking_code AS "bookingCode",b.booking_type AS "bookingType",b.status,
      c.id AS "containerId",c.container_no AS "containerNumber",car.company_name AS "carrierName"
      FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id JOIN containers c ON c.id=bc.container_id JOIN carriers car ON car.id=b.carrier_id
      WHERE bc.container_visit_id IS NULL AND c.container_no LIKE $1 AND b.status IN ('pending','ready','approved') ORDER BY b.appointment_start LIMIT 50`,[containerNumber+'%'])).rows;
  }
  async legacyOptions(bookingId:string,containerId:string) {
    const declarations=(await query(`SELECT d.id,d.customer_name AS "customerName",d.created_at AS "createdAt" FROM container_declarations d
      JOIN bookings b ON b.carrier_id=d.carrier_id WHERE b.id=$1 AND d.container_id=$2 ORDER BY d.created_at DESC LIMIT 50`,[bookingId,containerId])).rows;
    const visits=(await query(`SELECT id,visit_reference AS "visitReference",visit_status AS "visitStatus" FROM container_visits WHERE container_id=$1 AND visit_status IN ('planned','active') ORDER BY created_at DESC LIMIT 50`,[containerId])).rows;
    return {declarations,visits};
  }
  async reconcile(bookingId:string,containerId:string,declarationId:string,visitId:string|null,userId:string) {
    const c=await getClient();
    try {
      await c.query('BEGIN');
      const booking=(await c.query("SELECT carrier_id,booking_type FROM bookings WHERE id=$1 AND status IN ('pending','ready','approved') FOR UPDATE",[bookingId])).rows[0];
      if(!booking)throw new AppError('Chỉ đối soát booking chưa thực hiện.',409);
      if(visitId)await c.query('INSERT INTO container_visit_access(container_visit_id,carrier_id,granted_by) VALUES($1,$2,$3) ON CONFLICT DO NOTHING',[visitId,booking.carrier_id,userId]);
      const result=await c.query(`UPDATE booking_containers SET declaration_id=$3,container_visit_id=$4 WHERE booking_id=$1 AND container_id=$2 AND container_visit_id IS NULL RETURNING container_visit_id`,[bookingId,containerId,declarationId,visitId]);
      if(!result.rowCount)throw new AppError('Liên kết không tồn tại hoặc đã được đối soát.',409);
      await c.query(`INSERT INTO audit_logs(user_id,action,module,target_table,target_id,new_data) VALUES($1,'reconcile_legacy_booking','container','bookings',$2,$3::jsonb)`,[userId,bookingId,JSON.stringify({containerId,declarationId,visitId:result.rows[0].container_visit_id})]);
      await c.query('COMMIT');return result.rows[0];
    }catch(e){await c.query('ROLLBACK');if((e as {code?:string}).code==='23514')throw new AppError((e as Error).message,422);throw e;}finally{c.release();}
  }

  async carriers(search: string) {
    if(search.trim().length<2) return [];
    return (await query("SELECT id,company_name AS name FROM carriers WHERE status='active' AND company_name ILIKE $1 ORDER BY company_name LIMIT 30",['%'+search.trim().replace(/[\\%_]/g,'')+'%'])).rows;
  }

  async grantVisit(visitId: string,carrierId: string, userId: string) {
    await query(`INSERT INTO container_visit_access(container_visit_id,carrier_id,granted_by) VALUES($1,$2,$3) ON CONFLICT DO NOTHING`,[visitId,carrierId,userId]);
    return { visitId,carrierId };
  }
}

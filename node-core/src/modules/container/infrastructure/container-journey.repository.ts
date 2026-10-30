import { getClient, query } from '../../../infrastructure/database/db';
import { AppError } from '../../../shared/errors/app-error';

export class ContainerJourneyRepository {
  async lookup(number: string) {
    if (!/^[A-Z]{4}\d{7}$/.test(number)) throw new AppError('Nhập đầy đủ số Container gồm 11 ký tự.', 422);
    const result = await query(`SELECT c.id,c.container_no AS "containerNumber",ct.id AS "containerTypeId",
      ct.code AS "containerTypeCode",ct.description AS "containerTypeDescription"
      FROM containers c JOIN container_types ct ON ct.id=c.container_type_id WHERE c.container_no=$1`, [number]);
    return result.rows[0] ?? null;
  }

  async search(filters: Record<string, unknown>, userId?: string) {
    const number = String(filters.containerNumber ?? '').trim().toUpperCase();
    const plate = String(filters.plateNumber ?? '').replace(/[^a-z0-9]/gi, '').toUpperCase();
    const customer = String(filters.customerName ?? '').trim();
    const from = filters.dateFrom ? new Date(String(filters.dateFrom)) : null;
    const to = filters.dateTo ? new Date(String(filters.dateTo)) : null;
    if ((from && !Number.isFinite(from.getTime())) || (to && !Number.isFinite(to.getTime())) || (from && to && from > to))
      throw new AppError('Khoảng thời gian không hợp lệ.', 422);
    if (userId && (plate || customer || from || to)) throw new AppError('Công ty vận tải chỉ được tìm theo số Container.', 403);
    if ((!number || number.length < 4) && !plate && customer.length < 3)
      throw new AppError('Nhập số Container (ít nhất 4 ký tự), biển số xe hoặc khách hàng (ít nhất 3 ký tự).', 422);
    if (number && !/^[A-Z0-9]{4,11}$/.test(number)) throw new AppError('Số Container tìm kiếm không hợp lệ.', 422);
    if (plate && plate.length < 4) throw new AppError('Biển số xe cần ít nhất 4 ký tự.', 422);
    const page = Math.max(1, Math.min(Number(filters.page) || 1, 10000));
    const limit = Math.max(1, Math.min(Number(filters.limit) || 20, 50));
    const params: unknown[] = [];
    const where: string[] = [];
    const add = (sql: string, value: unknown) => { params.push(value); where.push(sql.replace('?', '$' + params.length)); };
    if (number) add('c.container_no LIKE ?', number + '%');
    if (userId) {
      // A separate projection makes cargo/customer/operational fields unreachable.
      const base = `FROM containers c JOIN container_types ct ON ct.id=c.container_type_id WHERE ${where.join(' AND ')}`;
      const total = Number((await query('SELECT count(*) ' + base, params)).rows[0].count);
      const rows = await query(`SELECT c.id,c.container_no AS "containerNumber",ct.code AS "containerTypeCode",ct.description AS "containerTypeDescription" ${base} ORDER BY c.container_no LIMIT $${params.length + 1} OFFSET $${params.length + 2}`, [...params,limit,(page-1)*limit]);
      return { items: rows.rows, total, page, limit, totalPages: Math.ceil(total/limit) };
    }
    if (customer) add("v.customer_name ILIKE ? ESCAPE '\\'", '%' + customer.replace(/[\\%_]/g, '\\$&') + '%');
    if (plate) add(`EXISTS(SELECT 1 FROM eir_transactions ex WHERE ex.container_visit_id=v.id AND regexp_replace(upper(ex.plate_number),'[^A-Z0-9]','','g') LIKE ?)
      OR EXISTS(SELECT 1 FROM booking_containers bx JOIN bookings b ON b.id=bx.booking_id LEFT JOIN trucks t ON t.id=b.truck_id
      WHERE bx.container_visit_id=v.id AND regexp_replace(upper(COALESCE(b.vehicle_plate,t.plate_number)),'[^A-Z0-9]','','g') LIKE ?)`, plate + '%');
    // The plate predicate uses the same parameter twice.
    if (plate) where[where.length-1] = '(' + where[where.length-1].replace('?', '$'+params.length) + ')';
    if (from) add('COALESCE(v.started_at,v.created_at)>=?',from);
    if (to) add('COALESCE(v.started_at,v.created_at)<?',to);
    const base = `FROM container_visits v JOIN containers c ON c.id=v.container_id JOIN container_types ct ON ct.id=c.container_type_id WHERE ${where.join(' AND ')}`;
    const total = Number((await query('SELECT count(*) ' + base,params)).rows[0].count);
    const rows = await query(`SELECT v.id,c.id AS "containerId",c.container_no AS "containerNumber",ct.code AS "containerTypeCode",
      v.visit_reference AS "visitReference",v.visit_status AS "visitStatus",v.started_at AS "startedAt",v.completed_at AS "completedAt",
      v.customer_name AS "customerName",v.seal_number AS "sealNumber",v.load_status AS "loadStatus",v.cargo_type AS "cargoType",
      v.gross_weight_kg::float8 AS "grossWeightKg",v.required_power_kw::float8 AS "requiredPowerKw",
      (SELECT json_agg(json_build_object('id',b.id,'bookingCode',b.booking_code,'bookingType',b.booking_type,'status',b.status,
       'plateNumber',COALESCE(b.vehicle_plate,t.plate_number),'appointmentStart',b.appointment_start,'appointmentEnd',b.appointment_end))
       FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id LEFT JOIN trucks t ON t.id=b.truck_id WHERE bc.container_visit_id=v.id) AS bookings,
      (SELECT json_agg(json_build_object('id',e.id,'referenceNo',e.reference_no,'activity',e.activity,'plateNumber',e.plate_number,
       'checkInAt',e.check_in_at,'checkOutAt',e.check_out_at,'locationCode',e.location_code,'customerName',e.customer_name,'cargoTypeName',e.cargo_type_description,'grossWeightKg',e.gross_weight_kg,'requiredPowerKw',e.required_power_kw,'sealNumber',(SELECT string_agg(s.seal_number,', ') FROM eir_seals s WHERE s.eir_transaction_id=e.id)))
       FROM eir_transactions e WHERE e.container_visit_id=v.id) AS receipts
      ${base} ORDER BY COALESCE(v.started_at,v.created_at) DESC,v.id LIMIT $${params.length+1} OFFSET $${params.length+2}`, [...params,limit,(page-1)*limit]);
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

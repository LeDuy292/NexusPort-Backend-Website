import { PoolClient } from 'pg';
import { getClient, query } from '../../../infrastructure/database/db';
import { AppError, ConflictError } from '../../../shared/errors/app-error';

export interface DriverRouteRow {
  containerId: string; containerNumber: string; bookingId: string;
  gateTransactionId: string; gateStatus: string; gateCode: string;
  slotId: string | null; zone: string | null; blockCode: string | null;
  bay: number | null; row: number | null; tier: number | null;
  instructionId: string | null; fromGate: string | null; routeDetail: string | null; mapData: unknown | null;
  operationId?: string | null; taskCode?: string | null; operationType?: string | null;
  operationStatus?: string | null; targetLocation?: string | null;
  destinationSource?: 'operation' | 'reservation' | 'container_position' | 'demo_admission'; slotStatus?: string | null;
  driverId?: string; driverName?: string; truckPlate?: string; bookingCode?: string; bookingType?: string; bookingStatus?: string;
  gateInAt?: string; gateOutAt?: string | null; containerConfirmedAt?: string | null;
  arrivedAt?: string | null; currentNodeId?: string | null; destinationKey?: string | null;
  yardCompletedAt?: string | null; exitGateCode?: string | null;
}

// The booking link is canonical: physical Gate In does not populate
// gate_transaction_containers. Never select a container's unrelated last trip.
export const driverRouteQuery = `
 SELECT DISTINCT ON (b.id,c.id)
 c.id AS "containerId",c.container_no AS "containerNumber",b.id AS "bookingId",
 b.booking_code AS "bookingCode",b.booking_type::text AS "bookingType",b.status::text AS "bookingStatus",
 gt.id AS "gateTransactionId",gt.status::text AS "gateStatus",g.code AS "gateCode",
 gt.checked_at AS "gateInAt",ex.checked_at AS "gateOutAt",
 gt.driver_id AS "driverId",d.full_name AS "driverName",COALESCE(t.plate_number,b.vehicle_plate) AS "truckPlate",
 dest.slot_id AS "slotId",dest.zone,dest.block_code AS "blockCode",dest.bay,dest.row_no AS row,dest.tier,dest.slot_status AS "slotStatus",
 op."Id" AS "operationId",op."TaskCode" AS "taskCode",op."OperationType" AS "operationType",op."Status" AS "operationStatus",
 COALESCE(NULLIF(btrim(op."ToLocation"),''),CASE WHEN dest.slot_id IS NOT NULL THEN concat(dest.block_code,'-',dest.bay,'-',dest.row_no,'-',dest.tier) END,
   CASE WHEN op."Id" IS NULL AND ri.map_data->>'source'='demo_admission' THEN ri.map_data->>'handoverLocation' END) AS "targetLocation",
 CASE WHEN op."Id" IS NOT NULL THEN 'operation' WHEN dest.reserved THEN 'reservation'
   WHEN dest.slot_id IS NOT NULL THEN 'container_position' WHEN ri.map_data->>'source'='demo_admission' THEN 'demo_admission' END AS "destinationSource",
 ri.id AS "instructionId",ri.from_gate AS "fromGate",ri.route_detail AS "routeDetail",ri.map_data AS "mapData",
 au.confirmed_at AS "containerConfirmedAt",ev.occurred_at AS "yardCompletedAt",
 pr.arrived_at AS "arrivedAt",vehicle_position.current_node_id AS "currentNodeId",pr.destination_key AS "destinationKey",
 exit_gate.code AS "exitGateCode"
 FROM bookings b
 JOIN booking_containers bc ON bc.booking_id=b.id
 JOIN containers c ON c.id=bc.container_id
 JOIN drivers d ON d.id=b.driver_id
 LEFT JOIN trucks t ON t.id=b.truck_id
 JOIN LATERAL (SELECT * FROM gate_transactions x WHERE x.booking_id=b.id AND x.driver_id=b.driver_id
   AND x.gate_type='gate_in' AND x.status='completed' ORDER BY x.checked_at DESC,x.id DESC LIMIT 1) gt ON true
 JOIN gates g ON g.id=gt.gate_id
 LEFT JOIN LATERAL (SELECT x.checked_at FROM gate_transactions x WHERE x.booking_id=b.id
   AND x.gate_type='gate_out' AND x.status='completed' AND x.checked_at>=gt.checked_at ORDER BY x.checked_at DESC LIMIT 1) ex ON true
 LEFT JOIN LATERAL (SELECT yt.* FROM yard_tasks yt WHERE yt."ContainerId"=c.id AND yt."DriverId"=gt.driver_id
   AND yt."IsDeleted"=false AND lower(yt."Status") NOT IN ('cancelled','canceled')
   AND ((yt.booking_id=b.id AND (yt.container_visit_id IS NULL OR yt.container_visit_id=bc.container_visit_id))
     OR (yt.booking_id IS NULL AND yt.container_visit_id=bc.container_visit_id AND yt."CreatedAt">=gt.checked_at)
     OR (yt.booking_id IS NULL AND yt.container_visit_id IS NULL AND yt."CreatedAt">=gt.checked_at
       AND NOT EXISTS(SELECT 1 FROM bookings ob JOIN booking_containers oc ON oc.booking_id=ob.id
         JOIN gate_transactions og ON og.booking_id=ob.id AND og.gate_type='gate_in' AND og.status='completed'
         WHERE ob.id<>b.id AND oc.container_id=c.id AND ob.driver_id=gt.driver_id
           AND NOT EXISTS(SELECT 1 FROM gate_transactions ox WHERE ox.booking_id=ob.id AND ox.gate_type='gate_out' AND ox.status='completed'))))
   AND yt."CreatedAt"<=COALESCE(ex.checked_at,now())
   ORDER BY (yt.booking_id=b.id) DESC NULLS LAST,yt."AssignedAt" DESC NULLS LAST,yt."CreatedAt" DESC,yt."Id" DESC LIMIT 1) op ON true
 LEFT JOIN LATERAL (
   SELECT ys.id AS slot_id,yb.id AS block_id,yb.zone,yb.code AS block_code,ys.bay,ys.row_no,ys.tier,ys.status::text AS slot_status,false AS reserved
   FROM yard_slots ys JOIN yard_blocks yb ON yb.id=ys.block_id
   CROSS JOIN LATERAL regexp_match(op."ToLocation",'^(.*)-([0-9]+)-([0-9]+)-([0-9]+)$') loc
   WHERE lower(yb.code)=lower(loc[1]) AND ys.bay=loc[2]::numeric AND ys.row_no=loc[3]::numeric AND ys.tier=loc[4]::numeric LIMIT 1
 ) operation_slot ON true
 LEFT JOIN LATERAL (
   SELECT ys.id AS slot_id,yb.id AS block_id,yb.zone,yb.code AS block_code,ys.bay,ys.row_no,ys.tier,ys.status::text AS slot_status,true AS reserved
   FROM yard_slot_reservations r JOIN yard_slots ys ON ys.id=r.slot_id JOIN yard_blocks yb ON yb.id=ys.block_id
   WHERE r.container_id=c.id AND r.reserved_from<=now() AND (r.reserved_to IS NULL OR r.reserved_to>now())
   AND r.reason='NXP-059 operation:'||op."Id"::text ORDER BY r.reserved_from DESC LIMIT 1
 ) reserved ON true
 LEFT JOIN LATERAL (
   SELECT ys.id AS slot_id,yb.id AS block_id,yb.zone,yb.code AS block_code,ys.bay,ys.row_no,ys.tier,ys.status::text AS slot_status,false AS reserved
   FROM container_positions cp JOIN yard_slots ys ON ys.id=cp.slot_id JOIN yard_blocks yb ON yb.id=ys.block_id
   JOIN container_visits v ON v.id=bc.container_visit_id AND v.container_id=c.id AND v.visit_status='active'
   WHERE cp.container_id=c.id AND cp.is_current=true AND b.booking_type::text='pickup'
   ORDER BY cp.placed_at DESC,cp.id DESC LIMIT 1
 ) placed ON true
 LEFT JOIN LATERAL (
   SELECT operation_slot.* WHERE operation_slot.slot_id IS NOT NULL
   UNION ALL SELECT reserved.* WHERE operation_slot.slot_id IS NULL AND NULLIF(btrim(op."ToLocation"),'') IS NULL AND reserved.slot_id IS NOT NULL
   UNION ALL SELECT placed.* WHERE operation_slot.slot_id IS NULL AND reserved.slot_id IS NULL AND NULLIF(btrim(op."ToLocation"),'') IS NULL
 ) dest ON true
 LEFT JOIN LATERAL (SELECT dri.* FROM driver_route_instructions dri WHERE dri.booking_id=b.id
   AND (dri.driver_id IS NULL OR dri.driver_id=gt.driver_id)
   AND (dri.map_data->>'containerId'=c.id::text OR (dri.map_data IS NULL AND dri.to_block_id=dest.block_id))
   ORDER BY dri.issued_at DESC,dri.id DESC LIMIT 1) ri ON true
 LEFT JOIN container_confirmation_audits au ON au.booking_id=b.id AND au.container_id=c.id AND au.driver_id=gt.driver_id
 LEFT JOIN LATERAL (SELECT e.occurred_at FROM yard_operation_events e WHERE e.container_id=c.id AND e.driver_id=gt.driver_id
   AND e.operation_status='Completed' AND e.occurred_at>=gt.checked_at
   AND (op."Id" IS NULL OR e.operation_id=op."Id") AND e.occurred_at<=COALESCE(ex.checked_at,now())
   ORDER BY e.occurred_at DESC LIMIT 1) ev ON true
 LEFT JOIN driver_route_progress pr ON pr.gate_transaction_id=gt.id AND pr.container_id=c.id
 LEFT JOIN LATERAL (SELECT p.current_node_id FROM driver_route_progress p WHERE p.gate_transaction_id=gt.id
   AND p.current_node_id IS NOT NULL ORDER BY p.updated_at DESC,p.container_id DESC LIMIT 1) vehicle_position ON true
 LEFT JOIN LATERAL (SELECT eg.code FROM gates eg WHERE eg.is_active AND eg.code='GATE-03' LIMIT 1) exit_gate ON true
 WHERE ($1::uuid IS NULL OR gt.driver_id=$1) AND ($2::uuid IS NULL OR c.id=$2)
   AND ($3::uuid IS NULL OR b.id=$3) AND b.status::text NOT IN ('canceled','rejected','expired')
   AND (ex.checked_at IS NULL OR ex.checked_at>now()-interval '24 hours')
 ORDER BY b.id,c.id,gt.checked_at DESC`;

export const routeDestinationKey = (row: DriverRouteRow) => `${row.operationId ?? ''}|${row.targetLocation ?? row.slotId ?? ''}`;

export class DriverRouteRepository {
  async resolveDriverId(accountId: string): Promise<string | null> {
    const result = await query(`SELECT d.id FROM drivers d WHERE (d.user_id=$1 OR (d.id=$1 AND d.user_id IS NULL))
      AND d.status::text NOT IN ('inactive','banned') LIMIT 1`, [accountId]);
    return result.rows[0]?.id ?? null;
  }
  async findForDriver(driverId: string): Promise<DriverRouteRow[]> {
    return (await query(driverRouteQuery,[driverId,null,null])).rows;
  }
  async findForDispatcher(): Promise<DriverRouteRow[]> {
    return (await query(driverRouteQuery,[null,null,null])).rows;
  }
  async findForDriverAndContainer(driverId: string,containerId: string,bookingId?: string): Promise<DriverRouteRow | null> {
    const rows = (await query(driverRouteQuery,[driverId,containerId,bookingId ?? null])).rows;
    if (rows.length>1) throw new ConflictError('Container có nhiều booking. Vui lòng chọn đúng booking.');
    return rows[0] ?? null;
  }
  private async locked(client: PoolClient,driverId: string,containerId: string,bookingId: string): Promise<DriverRouteRow> {
    await client.query('SELECT id FROM bookings WHERE id=$1 AND driver_id=$2 FOR UPDATE',[bookingId,driverId]);
    const row = (await client.query(driverRouteQuery,[driverId,containerId,bookingId])).rows[0];
    if (!row) throw new AppError('Không tìm thấy lượt xe đã qua cổng.',404,'DRIVER_ROUTE_NOT_FOUND');
    if (row.gateOutAt) throw new ConflictError('Xe đã ra cổng.');
    return row;
  }
  async arrive(driverId: string,containerId: string,bookingId: string,nodeId: string,destinationKey: string): Promise<void> {
    const client=await getClient();
    try {
      await client.query('BEGIN');
      const row=await this.locked(client,driverId,containerId,bookingId);
      if (row.containerConfirmedAt || routeDestinationKey(row)!==destinationKey) throw new ConflictError('Điểm đến đã thay đổi. Vui lòng tải lại lộ trình.');
      await client.query(`INSERT INTO driver_route_progress(gate_transaction_id,container_id,arrived_at,current_node_id,destination_key,updated_at)
        VALUES($1,$2,now(),$3,$4,now()) ON CONFLICT(gate_transaction_id,container_id) DO UPDATE
        SET arrived_at=CASE WHEN driver_route_progress.destination_key=excluded.destination_key THEN driver_route_progress.arrived_at ELSE excluded.arrived_at END,
        current_node_id=excluded.current_node_id,destination_key=excluded.destination_key,updated_at=now()`,[row.gateTransactionId,containerId,nodeId,destinationKey]);
      await client.query('COMMIT');
    } catch(error){await client.query('ROLLBACK');throw error;}finally{client.release();}
  }
  async confirm(driverId: string,containerId: string,bookingId: string,condition: string,notes?: string): Promise<void> {
    const client=await getClient();
    try {
      await client.query('BEGIN');
      const row=await this.locked(client,driverId,containerId,bookingId);
      if (!row.containerConfirmedAt) {
        if (row.bookingStatus !== 'checked_in') throw new ConflictError('Booking cần đang thực hiện giao nhận.');
        if (!row.yardCompletedAt) throw new ConflictError('Chưa hoàn tất thao tác bãi của booking này.');
        if (!row.arrivedAt || row.destinationKey!==routeDestinationKey(row)) throw new ConflictError('Vui lòng xác nhận đã đến đúng điểm giao nhận.');
        await client.query(`INSERT INTO container_confirmation_audits(id,booking_id,container_id,driver_id,condition,notes,container_status_after,confirmed_at,created_at)
          VALUES(gen_random_uuid(),$1,$2,$3,$4,$5,$6,now(),now()) ON CONFLICT(booking_id,container_id) DO NOTHING`,
          [bookingId,containerId,driverId,condition,notes ?? null,row.bookingType==='dropoff'?'in_yard':'moving']);
        await client.query(`UPDATE containers SET status=$2::container_status,updated_at=now() WHERE id=$1`,[containerId,row.bookingType==='dropoff'?'in_yard':'moving']);
        await client.query(`UPDATE bookings b SET status='completed' WHERE b.id=$1 AND b.status='checked_in'
          AND NOT EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.booking_id=b.id AND NOT EXISTS(
            SELECT 1 FROM container_confirmation_audits a WHERE a.booking_id=b.id AND a.container_id=bc.container_id))`,[bookingId]);
      }
      await client.query('COMMIT');
    } catch(error){await client.query('ROLLBACK');throw error;}finally{client.release();}
  }
}

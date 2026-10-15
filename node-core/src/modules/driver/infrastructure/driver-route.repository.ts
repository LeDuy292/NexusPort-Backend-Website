import { query } from '../../../infrastructure/database/db';

export interface DriverRouteRow {
  containerId: string;
  containerNumber: string;
  bookingId: string;
  gateTransactionId: string;
  gateStatus: string;
  gateCode: string;
  slotId: string | null;
  zone: string | null;
  blockCode: string | null;
  bay: number | null;
  row: number | null;
  tier: number | null;
  instructionId: string | null;
  fromGate: string | null;
  routeDetail: string | null;
  mapData: unknown | null;
  operationId?: string | null;
  taskCode?: string | null;
  operationType?: string | null;
  operationStatus?: string | null;
  targetLocation?: string | null;
  destinationSource?: 'operation' | 'reservation' | 'container_position';
  slotStatus?: string | null;
}

// Gate PASS is represented by the existing PostgreSQL gate_status values allowed/completed.
// A route is scoped to a container through gate_transaction_containers and its slot reservation.
export const driverRouteQuery = `
  SELECT DISTINCT ON (c.id)
         c.id AS "containerId", c.container_no AS "containerNumber",
         b.id AS "bookingId", gt.id AS "gateTransactionId",
         gt.status::text AS "gateStatus", g.code AS "gateCode",
         dest.slot_id AS "slotId", dest.zone, dest.block_code AS "blockCode",
         dest.bay, dest.row_no AS row, dest.tier, dest.slot_status AS "slotStatus",
         op."Id" AS "operationId", op."TaskCode" AS "taskCode",
         op."OperationType" AS "operationType", op."Status" AS "operationStatus",
         CASE WHEN op."Id" IS NOT NULL THEN NULLIF(btrim(op."ToLocation"), '')
              ELSE concat(dest.block_code, '-', dest.bay, '-', dest.row_no, '-', dest.tier)
          END AS "targetLocation",
         CASE WHEN op."Id" IS NOT NULL THEN 'operation'
              WHEN dest.reserved THEN 'reservation' ELSE 'container_position' END AS "destinationSource",
         ri.id AS "instructionId", ri.from_gate AS "fromGate",
         ri.route_detail AS "routeDetail", ri.map_data AS "mapData"
    FROM gate_transactions gt
    JOIN gate_transaction_containers gtc ON gtc.gate_transaction_id = gt.id
    JOIN containers c ON c.id = gtc.container_id
    JOIN bookings b ON b.id = gt.booking_id
    JOIN booking_containers bc ON bc.booking_id = b.id AND bc.container_id = c.id
    JOIN gates g ON g.id = gt.gate_id
    LEFT JOIN LATERAL (
      SELECT yt.*
        FROM yard_tasks yt
       WHERE yt."ContainerId" = c.id AND yt."DriverId" = gt.driver_id
         AND yt."IsDeleted" = false
         AND lower(yt."Status") NOT IN ('completed', 'cancelled', 'canceled')
       ORDER BY yt."AssignedAt" DESC NULLS LAST, yt."UpdatedAt" DESC NULLS LAST,
                yt."CreatedAt" DESC, yt."Id" DESC LIMIT 1
    ) op ON true
    LEFT JOIN LATERAL (
      SELECT ys.id AS slot_id, yb.id AS block_id, yb.zone, yb.code AS block_code,
             ys.bay, ys.row_no, ys.tier, ys.status::text AS slot_status, true AS reserved
        FROM yard_slot_reservations ysr
        JOIN yard_slots ys ON ys.id = ysr.slot_id
        JOIN yard_blocks yb ON yb.id = ys.block_id
       WHERE ysr.container_id = c.id
         AND ysr.reserved_from <= now()
         AND (ysr.reserved_to IS NULL OR ysr.reserved_to > now())
         AND (op."Id" IS NULL OR ysr.reason = 'NXP-059 operation:' || op."Id"::text)
       ORDER BY ysr.reserved_from DESC, ysr.id DESC LIMIT 1
    ) reserved ON true
    LEFT JOIN LATERAL (
      SELECT ys.id AS slot_id, yb.id AS block_id, yb.zone, yb.code AS block_code,
             ys.bay, ys.row_no, ys.tier, ys.status::text AS slot_status, false AS reserved
        FROM container_positions cp
        JOIN yard_slots ys ON ys.id = cp.slot_id
        JOIN yard_blocks yb ON yb.id = ys.block_id
       WHERE cp.container_id = c.id AND cp.is_current = true AND op."Id" IS NULL
       ORDER BY cp.placed_at DESC, cp.id DESC LIMIT 1
    ) placed ON true
    LEFT JOIN LATERAL (
      SELECT ys.id AS slot_id, yb.id AS block_id, yb.zone, yb.code AS block_code,
             ys.bay, ys.row_no, ys.tier, ys.status::text AS slot_status, false AS reserved
        FROM yard_slots ys
        JOIN yard_blocks yb ON yb.id = ys.block_id
        CROSS JOIN LATERAL regexp_match(op."ToLocation", '^(.*)-([0-9]+)-([0-9]+)-([0-9]+)$') loc
       WHERE op."Id" IS NOT NULL AND lower(yb.code) = lower(loc[1])
         AND ys.bay = loc[2]::numeric AND ys.row_no = loc[3]::numeric AND ys.tier = loc[4]::numeric
       LIMIT 1
    ) operation_slot ON true
    LEFT JOIN LATERAL (
      SELECT operation_slot.* WHERE operation_slot.slot_id IS NOT NULL
      UNION ALL
      SELECT reserved.* WHERE operation_slot.slot_id IS NULL AND reserved.slot_id IS NOT NULL
        AND (op."Id" IS NULL OR NULLIF(btrim(op."ToLocation"), '') IS NULL)
      UNION ALL
      SELECT placed.* WHERE operation_slot.slot_id IS NULL AND reserved.slot_id IS NULL
        AND op."Id" IS NULL
    ) dest ON true
    LEFT JOIN LATERAL (
      SELECT dri.id, dri.from_gate, dri.route_detail, dri.map_data
        FROM driver_route_instructions dri
       WHERE dri.booking_id = b.id
         AND (dri.driver_id IS NULL OR dri.driver_id = gt.driver_id)
         AND (dri.to_block_id IS NULL OR dri.to_block_id = dest.block_id)
         AND (dri.map_data->>'containerId' = c.id::text OR
              (dri.map_data IS NULL AND dri.to_block_id = dest.block_id))
       ORDER BY dri.issued_at DESC, dri.id DESC LIMIT 1
    ) ri ON true
   WHERE gt.driver_id = $1
     AND gt.gate_type = 'gate_in'
     AND gt.status IN ('allowed', 'completed')
     AND c.status NOT IN ('gate_out', 'loaded', 'canceled')
     AND NOT EXISTS (
       SELECT 1 FROM gate_transaction_containers later_gtc
       JOIN gate_transactions later_gt ON later_gt.id = later_gtc.gate_transaction_id
       WHERE later_gtc.container_id = c.id
         AND (later_gt.checked_at, later_gt.id) > (gt.checked_at, gt.id)
     )
     AND ($2::uuid IS NULL OR c.id = $2::uuid)
   ORDER BY c.id, gt.checked_at DESC, gt.id DESC`;

export class DriverRouteRepository {
  async findForDriver(driverId: string): Promise<DriverRouteRow[]> {
    const result = await query(driverRouteQuery, [driverId, null]);
    return result.rows as DriverRouteRow[];
  }

  async findForDriverAndContainer(driverId: string, containerId: string): Promise<DriverRouteRow | null> {
    const result = await query(driverRouteQuery, [driverId, containerId]);
    return (result.rows[0] as DriverRouteRow | undefined) ?? null;
  }
}

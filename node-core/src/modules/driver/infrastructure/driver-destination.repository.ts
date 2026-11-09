import { query } from '../../../infrastructure/database/db';
import { DestinationAssignment, YardSlotStatus } from '../../dispatcher/application/destination.dto';

const mapAssignment = (row: Record<string, unknown>): DestinationAssignment => ({
  operationId: String(row.operationId),
  taskCode: String(row.taskCode),
  operationType: String(row.operationType),
  operationStatus: String(row.operationStatus),
  container: { id: String(row.containerId), number: String(row.containerNumber) },
  driverId: String(row.driverId),
  destination: {
    slotId: String(row.slotId), blockId: String(row.blockId), block: String(row.block),
    blockName: String(row.blockName), zone: row.zone == null ? null : String(row.zone),
    bay: Number(row.bay), row: Number(row.row), tier: Number(row.tier),
    status: String(row.status) as YardSlotStatus,
    maxWeightKg: row.maxWeightKg == null ? null : Number(row.maxWeightKg),
    hasReeferPlug: Boolean(row.hasReeferPlug),
  },
  assignedAt: new Date(String(row.assignedAt)),
  assignedBy: row.assignedBy == null ? null : String(row.assignedBy),
});

export class DriverDestinationRepository {
  async findForDriver(driverId: string): Promise<DestinationAssignment[]> {
    const result = await query(
      `SELECT yt."Id" AS "operationId", yt."TaskCode" AS "taskCode",
              yt."OperationType" AS "operationType", yt."Status" AS "operationStatus",
              yt."ContainerId" AS "containerId", yt."ContainerNo" AS "containerNumber",
              yt."DriverId" AS "driverId", ysr.reserved_from AS "assignedAt",
              yt."AssignedBy" AS "assignedBy",
              ys.id AS "slotId", yb.id AS "blockId", yb.code AS block,
              yb.name AS "blockName", yb.zone, ys.bay, ys.row_no AS row, ys.tier,
              ys.status::text AS status, ys.max_weight_kg AS "maxWeightKg",
              ys.has_reefer_plug AS "hasReeferPlug"
         FROM yard_tasks yt
         JOIN yard_slot_reservations ysr
           ON ysr.container_id = yt."ContainerId"
          AND ysr.reserved_to IS NULL
          AND ysr.reason = 'NXP-059 operation:' || yt."Id"::text
         JOIN yard_slots ys ON ys.id = ysr.slot_id
         JOIN yard_blocks yb ON yb.id = ys.block_id
        WHERE yt."DriverId" = $1 AND yt."IsDeleted" = false
          AND lower(yt."Status") NOT IN ('cancelled', 'canceled')
        ORDER BY ysr.reserved_from DESC, yt."Id"`,
      [driverId],
    );
    return result.rows.map(mapAssignment);
  }
}

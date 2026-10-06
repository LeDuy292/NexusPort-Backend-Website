import { PoolClient } from 'pg';
import { getClient, query } from '../../../infrastructure/database/db';
import { AppError, ConflictError, NotFoundError } from '../../../shared/errors/app-error';
import { AssignDestinationInput, DestinationAssignment, YardDestination, YardSlotStatus } from '../application/destination.dto';

type OperationRow = {
  operationId: string;
  containerId: string | null;
  driverId: string | null;
  operationStatus: string;
};

type SlotRow = YardDestination;

const assignmentReason = (operationId: string) => `NXP-059 operation:${operationId}`;

const assignmentSelect = `
  SELECT yt."Id" AS "operationId", yt."TaskCode" AS "taskCode",
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
    JOIN yard_blocks yb ON yb.id = ys.block_id`;

const mapAssignment = (row: Record<string, unknown>): DestinationAssignment => ({
  operationId: String(row.operationId),
  taskCode: String(row.taskCode),
  operationType: String(row.operationType),
  operationStatus: String(row.operationStatus),
  container: { id: String(row.containerId), number: String(row.containerNumber) },
  driverId: String(row.driverId),
  destination: {
    slotId: String(row.slotId),
    blockId: String(row.blockId),
    block: String(row.block),
    blockName: String(row.blockName),
    zone: row.zone == null ? null : String(row.zone),
    bay: Number(row.bay),
    row: Number(row.row),
    tier: Number(row.tier),
    status: String(row.status) as YardSlotStatus,
    maxWeightKg: row.maxWeightKg == null ? null : Number(row.maxWeightKg),
    hasReeferPlug: Boolean(row.hasReeferPlug),
  },
  assignedAt: new Date(String(row.assignedAt)),
  assignedBy: row.assignedBy == null ? null : String(row.assignedBy),
});

export class DestinationRepository {
  async findYardDestinations(status?: YardSlotStatus): Promise<YardDestination[]> {
    const result = await query(
      `SELECT ys.id AS "slotId", yb.id AS "blockId", yb.code AS block,
              yb.name AS "blockName", yb.zone, ys.bay, ys.row_no AS row, ys.tier,
              ys.status::text AS status, ys.max_weight_kg AS "maxWeightKg",
              ys.has_reefer_plug AS "hasReeferPlug"
         FROM yard_slots ys
         JOIN yard_blocks yb ON yb.id = ys.block_id
        WHERE ($1::text IS NULL OR ys.status::text = $1)
        ORDER BY yb.code, ys.bay, ys.row_no, ys.tier`,
      [status ?? null],
    );
    return result.rows.map((row) => ({
      ...(row as SlotRow),
      bay: Number(row.bay), row: Number(row.row), tier: Number(row.tier),
      maxWeightKg: row.maxWeightKg == null ? null : Number(row.maxWeightKg),
    }));
  }

  async findAssignment(operationId: string): Promise<DestinationAssignment | null> {
    const result = await query(`${assignmentSelect} WHERE yt."Id" = $1 AND yt."IsDeleted" = false`, [operationId]);
    return result.rows[0] ? mapAssignment(result.rows[0]) : null;
  }

  async assign(input: AssignDestinationInput): Promise<DestinationAssignment> {
    const client = await getClient();
    try {
      await client.query('BEGIN');
      const operationResult = await client.query<OperationRow>(
        `SELECT "Id" AS "operationId", "ContainerId" AS "containerId",
                "DriverId" AS "driverId", "Status" AS "operationStatus"
           FROM yard_tasks
          WHERE "Id" = $1 AND "IsDeleted" = false
          FOR UPDATE`,
        [input.operationId],
      );
      const operation = operationResult.rows[0];
      if (!operation) throw new NotFoundError('Operation', input.operationId);
      if (!operation.containerId || !operation.driverId) {
        throw new ConflictError('Operation phải có Container và Driver trước khi gán destination.');
      }
      if (['completed', 'cancelled', 'canceled'].includes(operation.operationStatus.toLowerCase())) {
        throw new ConflictError('Không thể đổi destination của Operation đã kết thúc.');
      }

      const slotResult = await client.query<SlotRow>(
        `SELECT ys.id AS "slotId", yb.id AS "blockId", yb.code AS block,
                yb.name AS "blockName", yb.zone, ys.bay, ys.row_no AS row, ys.tier,
                ys.status::text AS status, ys.max_weight_kg AS "maxWeightKg",
                ys.has_reefer_plug AS "hasReeferPlug"
           FROM yard_slots ys
           JOIN yard_blocks yb ON yb.id = ys.block_id
          WHERE ys.id = $1
          FOR UPDATE OF ys`,
        [input.slotId],
      );
      const slot = slotResult.rows[0];
      if (!slot) throw new NotFoundError('Yard destination', input.slotId);

      const activeForSlot = await client.query<{ containerId: string; reason: string | null }>(
        `SELECT container_id AS "containerId", reason
           FROM yard_slot_reservations
          WHERE slot_id = $1 AND reserved_to IS NULL
          ORDER BY reserved_from DESC
          LIMIT 1 FOR UPDATE`,
        [input.slotId],
      );
      const active = activeForSlot.rows[0];
      const sameAssignment = active?.containerId === operation.containerId
        && active.reason === assignmentReason(input.operationId);
      if ((slot.status !== 'empty' || active) && !sameAssignment) {
        throw new ConflictError('Yard destination không còn trống.');
      }

      if (!sameAssignment) {
        const previous = await client.query<{ slotId: string }>(
          `UPDATE yard_slot_reservations
              SET reserved_to = now()
            WHERE container_id = $1 AND reserved_to IS NULL
          RETURNING slot_id AS "slotId"`,
          [operation.containerId],
        );
        const previousSlotIds = previous.rows.map((row) => row.slotId).filter((id) => id !== input.slotId);
        if (previousSlotIds.length) await this.releaseUnusedSlots(client, previousSlotIds);

        await client.query(
          `INSERT INTO yard_slot_reservations
             (slot_id, container_id, reserved_by, reserved_from, reason)
           VALUES ($1, $2, (SELECT id FROM users WHERE id = $3), now(), $4)`,
          [input.slotId, operation.containerId, input.dispatcherId, assignmentReason(input.operationId)],
        );
        await client.query(`UPDATE yard_slots SET status = 'reserved' WHERE id = $1`, [input.slotId]);
      }

      const location = `${slot.block}-${slot.bay}-${slot.row}-${slot.tier}`;
      await client.query(
        `UPDATE yard_tasks
            SET "ToLocation" = $2, "BlockCode" = $3, "AssignedAt" = now(),
                "AssignedBy" = $4, "UpdatedAt" = now(), "UpdatedBy" = $4
          WHERE "Id" = $1`,
        [input.operationId, location, slot.block, input.dispatcherName],
      );

      const assigned = await client.query(`${assignmentSelect} WHERE yt."Id" = $1 AND yt."IsDeleted" = false`, [input.operationId]);
      if (!assigned.rows[0]) throw new AppError('Không thể đọc lại destination vừa gán.', 500, 'DESTINATION_ASSIGNMENT_FAILED');
      await client.query('COMMIT');
      return mapAssignment(assigned.rows[0]);
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  private async releaseUnusedSlots(client: PoolClient, slotIds: string[]): Promise<void> {
    await client.query(
      `UPDATE yard_slots ys
          SET status = 'empty'
        WHERE ys.id = ANY($1::uuid[]) AND ys.status = 'reserved'
          AND NOT EXISTS (
            SELECT 1 FROM yard_slot_reservations ysr
             WHERE ysr.slot_id = ys.id AND ysr.reserved_to IS NULL
          )
          AND NOT EXISTS (
            SELECT 1 FROM container_positions cp
             WHERE cp.slot_id = ys.id AND cp.is_current = true
          )`,
      [slotIds],
    );
  }
}

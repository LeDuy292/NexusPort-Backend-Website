import { randomUUID } from 'crypto';
import { PoolClient } from 'pg';
import { getClient, query } from '../../../infrastructure/database/db';
import { ConflictError, NotFoundError } from '../../../shared/errors/app-error';
import {
  ConfirmContainerReceptionDto, YardActor, YardSlotDto,
} from '../application/yard-container.dto';

type ContainerRow = {
  id: string;
  containerNumber: string;
  sealNumber: string | null;
  status: string;
  grossWeightKg: string | number | null;
  isReefer: boolean;
  isDangerous: boolean;
  isOversized: boolean;
  movementType: string | null;
  visitId: string | null;
  visitSealNumber: string | null;
};

type SlotRow = YardSlotDto & {
  isReeferArea: boolean;
  isDangerousArea: boolean;
  isOversizedArea: boolean;
  activeReservationContainerId: string | null;
  currentContainerId: string | null;
  unsupportedTier: boolean;
  unoccupiedLowerTier: boolean;
};

const containerSelect = `
  SELECT c.id, c.container_no AS "containerNumber", c.seal_no AS "sealNumber",
         c.status::text AS status, c.gross_weight_kg AS "grossWeightKg",
         c.is_reefer AS "isReefer", c.is_dangerous AS "isDangerous",
         c.is_oversized AS "isOversized", cv.id AS "visitId",
         cv.movement_type AS "movementType", cv.seal_number AS "visitSealNumber"
    FROM containers c
    LEFT JOIN LATERAL (
      SELECT id, movement_type, seal_number
        FROM container_visits
       WHERE container_id = c.id AND visit_status IN ('planned', 'active')
       ORDER BY COALESCE(started_at, created_at) DESC, created_at DESC
       LIMIT 1
    ) cv ON true`;

const slotSelect = `
  SELECT ys.id AS "slotId", yb.id AS "blockId", yb.code AS "blockCode",
         yb.name AS "blockName", yb.zone, ys.bay, ys.row_no AS row, ys.tier,
         ys.status::text AS status, ys.max_weight_kg AS "maxWeightKg",
         ys.has_reefer_plug AS "hasReeferPlug", yb.is_reefer_area AS "isReeferArea",
         yb.is_dangerous_area AS "isDangerousArea", yb.is_oversized_area AS "isOversizedArea",
         reservation.container_id AS "activeReservationContainerId",
         position.container_id AS "currentContainerId",
         EXISTS (
           SELECT 1 FROM yard_slots lower_slot
            WHERE lower_slot.block_id = ys.block_id AND lower_slot.bay = ys.bay
              AND lower_slot.row_no = ys.row_no AND lower_slot.tier < ys.tier
              AND NOT EXISTS (
                SELECT 1 FROM container_positions lower_position
                 WHERE lower_position.slot_id = lower_slot.id AND lower_position.is_current = true
              )
              AND NOT EXISTS (
                SELECT 1 FROM yard_slot_reservations lower_reservation
                 WHERE lower_reservation.slot_id = lower_slot.id AND lower_reservation.reserved_to IS NULL
              )
         ) AS "unsupportedTier"
         , EXISTS (
           SELECT 1 FROM yard_slots lower_slot
            WHERE lower_slot.block_id = ys.block_id AND lower_slot.bay = ys.bay
              AND lower_slot.row_no = ys.row_no AND lower_slot.tier < ys.tier
              AND NOT EXISTS (
                SELECT 1 FROM container_positions lower_position
                 WHERE lower_position.slot_id = lower_slot.id AND lower_position.is_current = true
              )
         ) AS "unoccupiedLowerTier"
    FROM yard_slots ys
    JOIN yard_blocks yb ON yb.id = ys.block_id
    LEFT JOIN LATERAL (
      SELECT container_id FROM yard_slot_reservations
       WHERE slot_id = ys.id AND reserved_to IS NULL
       ORDER BY reserved_from DESC LIMIT 1
    ) reservation ON true
    LEFT JOIN LATERAL (
      SELECT container_id FROM container_positions
       WHERE slot_id = ys.id AND is_current = true
       ORDER BY placed_at DESC LIMIT 1
    ) position ON true`;

const mapSlot = (row: SlotRow, containerId: string): YardSlotDto => ({
  slotId: String(row.slotId), blockId: String(row.blockId), blockCode: String(row.blockCode),
  blockName: String(row.blockName), zone: row.zone == null ? null : String(row.zone),
  bay: Number(row.bay), row: Number(row.row), tier: Number(row.tier), status: row.status,
  maxWeightKg: row.maxWeightKg == null ? null : Number(row.maxWeightKg),
  hasReeferPlug: Boolean(row.hasReeferPlug),
  reservedForContainer: row.activeReservationContainerId === containerId,
});

export const assertYardSlotCompatible = (
  container: ContainerRow, slot: SlotRow, requireOccupiedSupport = false,
): void => {
  if (slot.currentContainerId && slot.currentContainerId !== container.id) {
    throw new ConflictError('Ô bãi đã có Container khác.');
  }
  if (slot.activeReservationContainerId && slot.activeReservationContainerId !== container.id) {
    throw new ConflictError('Ô bãi đã được đặt trước cho Container khác.');
  }
  const sameContainer = slot.currentContainerId === container.id || slot.activeReservationContainerId === container.id;
  if (!sameContainer && slot.status !== 'empty') {
    throw new ConflictError(`Không thể dùng ô bãi có trạng thái '${slot.status}'.`);
  }
  const grossWeight = container.grossWeightKg == null ? null : Number(container.grossWeightKg);
  if (grossWeight != null && slot.maxWeightKg != null && grossWeight > Number(slot.maxWeightKg)) {
    throw new ConflictError('Trọng lượng Container vượt tải trọng tối đa của ô bãi.');
  }
  if (container.isReefer && (!slot.isReeferArea || !slot.hasReeferPlug)) {
    throw new ConflictError('Container lạnh phải được xếp vào khu lạnh có ổ cắm điện.');
  }
  if (container.isDangerous && !slot.isDangerousArea) {
    throw new ConflictError('Container hàng nguy hiểm phải được xếp vào khu hàng nguy hiểm.');
  }
  if (container.isOversized && !slot.isOversizedArea) {
    throw new ConflictError('Container quá khổ phải được xếp vào khu hàng quá khổ.');
  }
  if (slot.unsupportedTier) {
    throw new ConflictError('Không thể xếp Container lên tầng khi các tầng bên dưới còn trống.');
  }
  if (requireOccupiedSupport && slot.unoccupiedLowerTier) {
    throw new ConflictError('Chưa thể đặt Container vì tầng bên dưới chưa có Container nâng đỡ.');
  }
};

const getContainer = async (client: PoolClient, containerId: string): Promise<ContainerRow> => {
  const result = await client.query<ContainerRow>(`${containerSelect} WHERE c.id = $1 FOR UPDATE OF c`, [containerId]);
  if (!result.rows[0]) throw new NotFoundError('Container', containerId);
  return result.rows[0];
};

const getSlot = async (client: PoolClient, slotId: string): Promise<SlotRow> => {
  const result = await client.query<SlotRow>(`${slotSelect} WHERE ys.id = $1 FOR UPDATE OF ys`, [slotId]);
  if (!result.rows[0]) throw new NotFoundError('Yard slot', slotId);
  return result.rows[0];
};

const audit = async (
  client: PoolClient, actor: YardActor, action: string, containerId: string,
  oldData: Record<string, unknown>, newData: Record<string, unknown>,
) => client.query(
  `INSERT INTO audit_logs (
     user_id, action, module, target_table, target_id, old_data, new_data, ip_address
   ) VALUES ($1, $2, 'yard', 'containers', $3, $4::jsonb, $5::jsonb, $6)`,
  [actor.id, action, containerId, JSON.stringify(oldData), JSON.stringify(newData), actor.ipAddress ?? null],
);

export class YardContainerRepository {
  async listAvailableSlots(containerId: string): Promise<YardSlotDto[]> {
    const containerResult = await query(`${containerSelect} WHERE c.id = $1`, [containerId]);
    const container = containerResult.rows[0] as ContainerRow | undefined;
    if (!container) throw new NotFoundError('Container', containerId);
    const result = await query(
      `${slotSelect}
       WHERE (ys.status = 'empty' OR reservation.container_id = $1)
         AND position.container_id IS NULL
         AND (reservation.container_id IS NULL OR reservation.container_id = $1)
         AND (ys.max_weight_kg IS NULL OR $2::numeric IS NULL OR ys.max_weight_kg >= $2::numeric)
         AND (NOT $3::boolean OR (yb.is_reefer_area AND ys.has_reefer_plug))
         AND (NOT $4::boolean OR yb.is_dangerous_area)
         AND (NOT $5::boolean OR yb.is_oversized_area)
       ORDER BY (reservation.container_id = $1) DESC, yb.code, ys.bay, ys.row_no, ys.tier`,
      [containerId, container.grossWeightKg, container.isReefer, container.isDangerous, container.isOversized],
    );
    return (result.rows as SlotRow[]).filter((slot) => !slot.unsupportedTier).map((slot) => mapSlot(slot, containerId));
  }

  async confirmReception(containerId: string, dto: ConfirmContainerReceptionDto, actor: YardActor) {
    const client = await getClient();
    try {
      await client.query('BEGIN');
      const container = await getContainer(client, containerId);
      if (container.containerNumber !== dto.containerNumber) {
        throw new ConflictError('Mã Container quét được không khớp với Container cần tiếp nhận.');
      }
      const expectedSeal = container.visitSealNumber ?? container.sealNumber;
      if (expectedSeal && expectedSeal.trim().toUpperCase() !== dto.sealNumber.trim().toUpperCase()) {
        throw new ConflictError('Số seal không khớp với dữ liệu đã khai báo.');
      }
      if (container.movementType === 'pickup_request') {
        throw new ConflictError('Lượt nhận Container không thể dùng quy trình tiếp nhận vào bãi.');
      }
      const permittedStatuses = ['expected', 'reserved', 'discharged', 'gate_in', 'in_yard'];
      if (!permittedStatuses.includes(container.status) && !(container.status === 'damaged' && dto.condition === 'damaged')) {
        throw new ConflictError(`Container ở trạng thái '${container.status}' không thể tiếp nhận vào bãi.`);
      }
      const targetStatus = dto.condition === 'damaged'
        ? 'damaged'
        : container.status === 'expected' || container.status === 'reserved'
          ? container.movementType === 'truck_dropoff' ? 'gate_in' : 'discharged'
          : container.status;
      await client.query(
        `UPDATE containers
            SET seal_no = COALESCE(seal_no, $2), status = $3::container_status,
                arrived_at = COALESCE(arrived_at, now()), updated_at = now()
          WHERE id = $1`,
        [containerId, dto.sealNumber, targetStatus],
      );
      if (container.visitId) {
        await client.query(
          `UPDATE container_visits
              SET seal_number = COALESCE(seal_number, $2), visit_status = 'active',
                  started_at = COALESCE(started_at, now()), updated_at = now()
            WHERE id = $1`,
          [container.visitId, dto.sealNumber],
        );
      }
      await audit(client, actor, 'container_reception_confirmed', containerId,
        { status: container.status, sealNumber: expectedSeal },
        { status: targetStatus, sealNumber: dto.sealNumber, condition: dto.condition, conditionNotes: dto.conditionNotes ?? null });
      await client.query('COMMIT');
      return {
        containerId, containerNumber: container.containerNumber, sealNumber: dto.sealNumber,
        condition: dto.condition, status: targetStatus, receivedAt: new Date(),
      };
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async reserveSlot(containerId: string, slotId: string, actor: YardActor) {
    const client = await getClient();
    try {
      await client.query('BEGIN');
      const container = await getContainer(client, containerId);
      if (!['expected', 'reserved', 'discharged', 'gate_in'].includes(container.status)) {
        throw new ConflictError(`Container ở trạng thái '${container.status}' không thể đặt trước vị trí.`);
      }
      const slot = await getSlot(client, slotId);
      assertYardSlotCompatible(container, slot);
      const existing = await client.query<{ slotId: string }>(
        `SELECT slot_id AS "slotId" FROM yard_slot_reservations
          WHERE container_id = $1 AND reserved_to IS NULL
          ORDER BY reserved_from DESC LIMIT 1 FOR UPDATE`,
        [containerId],
      );
      if (existing.rows[0] && existing.rows[0].slotId !== slotId) {
        throw new ConflictError('Container đã có một vị trí đặt trước đang hiệu lực.');
      }
      if (!existing.rows[0]) {
        await client.query(
          `INSERT INTO yard_slot_reservations (slot_id, container_id, reserved_by, reserved_from, reason)
           VALUES ($1, $2, $3, now(), 'NXP-087 pre-arrival')`,
          [slotId, containerId, actor.id],
        );
        await client.query(`UPDATE yard_slots SET status = 'reserved' WHERE id = $1`, [slotId]);
        await audit(client, actor, 'yard_slot_reserved', containerId, {}, { slotId });
      }
      await client.query('COMMIT');
      return { containerId, slot: mapSlot({ ...slot, status: 'reserved', activeReservationContainerId: containerId }, containerId) };
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async placeContainer(containerId: string, slotId: string, actor: YardActor) {
    const client = await getClient();
    try {
      await client.query('BEGIN');
      const container = await getContainer(client, containerId);
      if (!['discharged', 'gate_in', 'in_yard', 'damaged'].includes(container.status)) {
        throw new ConflictError('Container phải được xác nhận tiếp nhận trước khi gán vị trí lưu.');
      }
      const slot = await getSlot(client, slotId);
      assertYardSlotCompatible(container, slot, true);
      const previous = await client.query<{ slotId: string }>(
        `UPDATE container_positions SET is_current = false, removed_at = COALESCE(removed_at, now())
          WHERE container_id = $1 AND is_current = true AND slot_id <> $2
        RETURNING slot_id AS "slotId"`,
        [containerId, slotId],
      );
      const current = await client.query<{ id: string }>(
        'SELECT id FROM container_positions WHERE container_id = $1 AND slot_id = $2 AND is_current = true FOR UPDATE',
        [containerId, slotId],
      );
      if (!current.rows[0]) {
        await client.query(
          `INSERT INTO container_positions (id, container_id, slot_id, placed_by, placed_at, is_current)
           VALUES ($1, $2, $3, $4, now(), true)`,
          [randomUUID(), containerId, slotId, actor.id],
        );
      }
      await client.query(
        'UPDATE yard_slot_reservations SET reserved_to = now() WHERE container_id = $1 AND reserved_to IS NULL',
        [containerId],
      );
      await client.query(`UPDATE yard_slots SET status = 'occupied' WHERE id = $1`, [slotId]);
      const previousSlotIds = previous.rows.map((row) => row.slotId);
      if (previousSlotIds.length) {
        await client.query(
          `UPDATE yard_slots ys SET status = 'empty'
            WHERE ys.id = ANY($1::uuid[]) AND NOT EXISTS (
              SELECT 1 FROM container_positions cp WHERE cp.slot_id = ys.id AND cp.is_current = true
            ) AND NOT EXISTS (
              SELECT 1 FROM yard_slot_reservations ysr WHERE ysr.slot_id = ys.id AND ysr.reserved_to IS NULL
            )`,
          [previousSlotIds],
        );
      }
      const targetStatus = container.status === 'damaged' ? 'damaged' : 'in_yard';
      await client.query(
        `UPDATE containers SET status = $2::container_status, arrived_at = COALESCE(arrived_at, now()), updated_at = now()
          WHERE id = $1`,
        [containerId, targetStatus],
      );
      await audit(client, actor, 'container_placed_in_yard', containerId,
        { status: container.status }, { status: targetStatus, slotId });
      await client.query('COMMIT');
      return { containerId, status: targetStatus, slot: mapSlot({ ...slot, status: 'occupied' }, containerId), placedAt: new Date() };
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }
}

import { query } from '../../../infrastructure/database/db';
import { CreateContainerDto, ContainerListItem, ContainerListResult, ContainerSearchDto, UpdateContainerDto } from '../application/container.dto';
import { ContainerBookingSummary, ContainerDetailEntity, ContainerPositionSummary, ContainerTypeEntity } from '../domain/container.entity';

const baseSelect = `
  SELECT c.id,
         c.carrier_id AS "carrierId",
         c.container_type_id AS "containerTypeId",
         c.vessel_call_id AS "vesselCallId",
         c.container_no AS "containerNumber",
         c.seal_no AS "sealNumber",
         c.cargo_type AS "cargoType",
         c.status,
         c.gross_weight_kg AS "grossWeightKg",
         c.is_reefer AS "isReefer",
         c.is_dangerous AS "isDangerous",
         c.is_perishable AS "isPerishable",
         c.is_oversized AS "isOversized",
         c.expected_gate_out_at AS "expectedGateOutAt",
         c.arrived_at AS "arrivedAt",
         c.left_at AS "leftAt",
         c.created_at AS "createdAt",
         c.updated_at AS "updatedAt",
         ct.code AS "typeCode",
         ct.size,
         ct.category,
         cr.company_name AS "carrierName",
         vc.call_code AS "vesselCallCode",
         (SELECT COUNT(*)::int FROM booking_containers bc WHERE bc.container_id = c.id) AS "bookingCount"
    FROM containers c
    JOIN container_types ct ON ct.id = c.container_type_id
    LEFT JOIN carriers cr ON cr.id = c.carrier_id
    LEFT JOIN vessel_calls vc ON vc.id = c.vessel_call_id`;

const numberOrNull = (value: unknown): number | null => value === null || value === undefined ? null : Number(value);

const mapType = (row: Record<string, unknown>): ContainerTypeEntity => ({
  id: String(row.id),
  code: String(row.code),
  size: row.size as ContainerTypeEntity['size'],
  category: row.category as ContainerTypeEntity['category'],
  description: row.description == null ? null : String(row.description),
  tareWeightKg: numberOrNull(row.tareWeightKg),
  maxGrossWeightKg: numberOrNull(row.maxGrossWeightKg),
});

const mapListRow = (row: Record<string, unknown>): ContainerListItem => ({
  ...row,
  grossWeightKg: numberOrNull(row.grossWeightKg),
  bookingCount: Number(row.bookingCount ?? 0),
} as ContainerListItem);

export class ContainerRepository {
  async findAll(filters: ContainerSearchDto): Promise<ContainerListResult> {
    const conditions: string[] = [];
    const values: unknown[] = [];
    const addCondition = (sql: string, value: unknown) => {
      values.push(value);
      conditions.push(sql.replace('?', `$${values.length}`));
    };

    if (!filters.includeCanceled && !filters.status) conditions.push("c.status <> 'canceled'");
    if (filters.search) {
      values.push(`%${filters.search}%`);
      conditions.push(`(c.container_no ILIKE $${values.length} OR c.seal_no ILIKE $${values.length})`);
    }
    if (filters.status) addCondition('c.status = ?', filters.status);
    if (filters.containerTypeId) addCondition('c.container_type_id = ?', filters.containerTypeId);
    if (filters.size) addCondition('ct.size = ?', filters.size);
    if (filters.category) addCondition('ct.category = ?', filters.category);
    if (filters.cargoType) addCondition('c.cargo_type = ?', filters.cargoType);
    if (filters.carrierId) addCondition('c.carrier_id = ?', filters.carrierId);

    const where = conditions.length ? `WHERE ${conditions.join(' AND ')}` : '';
    const sortColumns: Record<ContainerSearchDto['sortBy'], string> = {
      containerNumber: 'c.container_no', status: 'c.status', createdAt: 'c.created_at', updatedAt: 'c.updated_at',
    };
    const offset = (filters.page - 1) * filters.limit;
    const listValues = [...values, filters.limit, offset];
    const listResult = await query(
      `${baseSelect}
       ${where}
       ORDER BY ${sortColumns[filters.sortBy]} ${filters.sortOrder.toUpperCase()}
       LIMIT $${values.length + 1} OFFSET $${values.length + 2}`,
      listValues,
    );
    const countResult = await query(
      `SELECT COUNT(*)::int AS total
         FROM containers c
         JOIN container_types ct ON ct.id = c.container_type_id
         ${where}`,
      values,
    );
    const total = Number(countResult.rows[0]?.total ?? 0);
    return {
      items: listResult.rows.map((row) => mapListRow(row)),
      total,
      page: filters.page,
      limit: filters.limit,
      totalPages: Math.ceil(total / filters.limit),
    };
  }

  async findById(id: string): Promise<ContainerDetailEntity | null> {
    const result = await query(`${baseSelect} WHERE c.id = $1`, [id]);
    if (!result.rows[0]) return null;
    const row = result.rows[0];

    const typeResult = await query(
      `SELECT id, code, size, category, description,
              tare_weight_kg AS "tareWeightKg",
              max_gross_weight_kg AS "maxGrossWeightKg"
         FROM container_types WHERE id = $1`,
      [row.containerTypeId],
    );
    const bookingResult = await query(
      `SELECT b.id, b.booking_code AS "bookingCode", b.booking_type AS "bookingType",
              b.status, b.appointment_start AS "appointmentStart", b.appointment_end AS "appointmentEnd"
         FROM booking_containers bc
         JOIN bookings b ON b.id = bc.booking_id
        WHERE bc.container_id = $1
        ORDER BY b.created_at DESC`,
      [id],
    );
    const positionResult = await query(
      `SELECT cp.slot_id AS "slotId", yb.code AS "blockCode", ys.bay,
              ys.row_no AS row, ys.tier, cp.placed_at AS "placedAt"
         FROM container_positions cp
         JOIN yard_slots ys ON ys.id = cp.slot_id
         JOIN yard_blocks yb ON yb.id = ys.block_id
        WHERE cp.container_id = $1 AND cp.is_current = true
        ORDER BY cp.placed_at DESC LIMIT 1`,
      [id],
    );

    return {
      ...mapListRow(row),
      containerType: mapType(typeResult.rows[0]),
      vesselCallCode: row.vesselCallCode == null ? null : String(row.vesselCallCode),
      bookings: bookingResult.rows as ContainerBookingSummary[],
      currentPosition: (positionResult.rows[0] as ContainerPositionSummary | undefined) ?? null,
    };
  }

  async findByContainerNumber(containerNumber: string): Promise<{ id: string } | null> {
    const result = await query('SELECT id FROM containers WHERE container_no = $1 LIMIT 1', [containerNumber]);
    return result.rows[0] ? { id: result.rows[0].id } : null;
  }

  async findTypeById(id: string): Promise<ContainerTypeEntity | null> {
    const result = await query(
      `SELECT id, code, size, category, description,
              tare_weight_kg AS "tareWeightKg", max_gross_weight_kg AS "maxGrossWeightKg"
         FROM container_types WHERE id = $1`,
      [id],
    );
    return result.rows[0] ? mapType(result.rows[0]) : null;
  }

  async findTypes(): Promise<ContainerTypeEntity[]> {
    const result = await query(
      `SELECT id, code, size, category, description,
              tare_weight_kg AS "tareWeightKg", max_gross_weight_kg AS "maxGrossWeightKg"
         FROM container_types ORDER BY size, category, code`,
    );
    return result.rows.map((row) => mapType(row));
  }

  async create(dto: CreateContainerDto, typeCategory: string): Promise<ContainerDetailEntity> {
    const result = await query(
      `INSERT INTO containers (
         carrier_id, container_type_id, vessel_call_id, container_no, seal_no, cargo_type, status,
         gross_weight_kg, is_reefer, is_dangerous, is_perishable, is_oversized, expected_gate_out_at
       ) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
       RETURNING id`,
      [dto.carrierId ?? null, dto.containerTypeId, dto.vesselCallId ?? null, dto.containerNumber,
       dto.sealNumber, dto.cargoType ?? 'general', dto.status ?? 'expected', dto.grossWeightKg ?? null,
       typeCategory === 'reefer' || dto.cargoType === 'reefer', dto.cargoType === 'dangerous',
       dto.cargoType === 'perishable', dto.cargoType === 'oversized', dto.expectedGateOutAt ?? null],
    );
    return (await this.findById(result.rows[0].id))!;
  }

  async update(id: string, dto: UpdateContainerDto, typeCategory: string, effectiveCargoType: string): Promise<ContainerDetailEntity> {
    const columnMap: Record<string, string> = {
      carrierId: 'carrier_id', containerTypeId: 'container_type_id', vesselCallId: 'vessel_call_id',
      containerNumber: 'container_no', sealNumber: 'seal_no', cargoType: 'cargo_type', status: 'status',
      grossWeightKg: 'gross_weight_kg', expectedGateOutAt: 'expected_gate_out_at', arrivedAt: 'arrived_at', leftAt: 'left_at',
    };
    const entries = Object.entries(dto).filter(([, value]) => value !== undefined);
    const values: unknown[] = entries.map(([, value]) => value);
    const assignments = entries.map(([key], index) => `${columnMap[key]} = $${index + 1}`);
    values.push(typeCategory === 'reefer' || effectiveCargoType === 'reefer');
    assignments.push(`is_reefer = $${values.length}`);
    values.push(effectiveCargoType === 'dangerous'); assignments.push(`is_dangerous = $${values.length}`);
    values.push(effectiveCargoType === 'perishable'); assignments.push(`is_perishable = $${values.length}`);
    values.push(effectiveCargoType === 'oversized'); assignments.push(`is_oversized = $${values.length}`);
    assignments.push('updated_at = now()');
    values.push(id);
    await query(`UPDATE containers SET ${assignments.join(', ')} WHERE id = $${values.length}`, values);
    return (await this.findById(id))!;
  }

  async softDelete(id: string): Promise<void> {
    await query("UPDATE containers SET status = 'canceled', updated_at = now() WHERE id = $1", [id]);
  }
}

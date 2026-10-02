import { getClient, query } from '../../../infrastructure/database/db';
import { CreateContainerDto, ContainerListItem, ContainerListResult, ContainerSearchDto, UpdateContainerDto } from '../application/container.dto';
import {
  ContainerBookingSummary, ContainerDetailEntity, ContainerEirDamage, ContainerEirMedia,
  ContainerEirSeal, ContainerEirTransaction, ContainerPositionSummary, ContainerStatus,
  ContainerStatusHistoryEntry, ContainerTypeEntity, ContainerVisit, PersistedContainerStatus,
} from '../domain/container.entity';

interface TransitionStatusInput {
  containerId: string;
  expectedStatus: PersistedContainerStatus;
  targetStatus: PersistedContainerStatus;
  fromStatus: ContainerStatus;
  toStatus: ContainerStatus;
  userId: string;
  ipAddress: string | null;
}

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
         ov.visit_reference AS "latestVisitReference",
         ov.visit_status AS "latestVisitStatus",
         ov.load_status AS "latestLoadStatus",
         ov.reference_no AS "latestEirReference",
         ov.activity AS "latestActivity",
         ov.location_code AS "latestLocationCode",
         ov.plate_number AS "latestPlateNumber",
         ov.check_in_at AS "latestCheckInAt",
         ov.check_out_at AS "latestCheckOutAt",
         ov.latest_seal AS "latestSealNumber",
         ov.gross_weight_kg AS "latestGrossWeightKg",
         (SELECT COUNT(*)::int FROM booking_containers bc WHERE bc.container_id = c.id) AS "bookingCount"
    FROM containers c
    JOIN container_types ct ON ct.id = c.container_type_id
    LEFT JOIN carriers cr ON cr.id = c.carrier_id
    LEFT JOIN vessel_calls vc ON vc.id = c.vessel_call_id
    LEFT JOIN LATERAL (
      SELECT cv.visit_reference, cv.visit_status, cv.load_status,
             e.reference_no, e.activity, e.location_code, e.plate_number,
             e.check_in_at, e.check_out_at, e.gross_weight_kg,
             (SELECT COALESCE(es.raw_value, es.seal_number)
                FROM eir_seals es
               WHERE es.eir_transaction_id = e.id
               ORDER BY es.sequence_no LIMIT 1) AS latest_seal
        FROM container_visits cv
        LEFT JOIN LATERAL (
          SELECT e.* FROM eir_transactions e
           WHERE e.container_visit_id = cv.id
           ORDER BY e.issued_at DESC, e.created_at DESC LIMIT 1
        ) e ON true
       WHERE cv.container_id = c.id
       ORDER BY COALESCE(cv.started_at, cv.created_at) DESC, cv.created_at DESC
       LIMIT 1
    ) ov ON true`;

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
  latestGrossWeightKg: numberOrNull(row.latestGrossWeightKg),
  bookingCount: Number(row.bookingCount ?? 0),
} as ContainerListItem);

export class ContainerRepository {
  async findCurrentStatus(id: string): Promise<{ status: PersistedContainerStatus; updatedAt: Date } | null> {
    const result = await query(
      'SELECT status, updated_at AS "updatedAt" FROM containers WHERE id = $1',
      [id],
    );
    return result.rows[0] ?? null;
  }

  async findStatusHistory(containerId: string): Promise<ContainerStatusHistoryEntry[]> {
    const result = await query(
      `SELECT a.id,
              a.target_id AS "containerId",
              a.old_data->>'status' AS "fromStatus",
              a.new_data->>'status' AS "toStatus",
              a.user_id AS "changedBy",
              COALESCE(u.full_name, u.username) AS "changedByName",
              a.created_at AS "changedAt"
         FROM audit_logs a
         LEFT JOIN users u ON u.id = a.user_id
        WHERE a.module = 'container'
          AND a.action = 'container_status_transition'
          AND a.target_table = 'containers'
          AND a.target_id = $1
        ORDER BY a.created_at ASC, a.id ASC`,
      [containerId],
    );
    return result.rows as ContainerStatusHistoryEntry[];
  }

  async transitionStatus(input: TransitionStatusInput): Promise<{
    updatedAt?: Date;
    conflictStatus?: PersistedContainerStatus;
  } | null> {
    const client = await getClient();
    try {
      await client.query('BEGIN');
      const locked = await client.query<{ status: PersistedContainerStatus }>(
        'SELECT status FROM containers WHERE id = $1 FOR UPDATE',
        [input.containerId],
      );
      if (!locked.rows[0]) {
        await client.query('ROLLBACK');
        return null;
      }
      if (locked.rows[0].status !== input.expectedStatus) {
        await client.query('ROLLBACK');
        return { conflictStatus: locked.rows[0].status };
      }

      const updated = await client.query<{ updatedAt: Date }>(
        `UPDATE containers
            SET status = $2::container_status,
                arrived_at = CASE WHEN $2::container_status = 'gate_in' THEN COALESCE(arrived_at, now()) ELSE arrived_at END,
                left_at = CASE WHEN $2::container_status = 'gate_out' THEN COALESCE(left_at, now()) ELSE left_at END,
                updated_at = now()
          WHERE id = $1
        RETURNING updated_at AS "updatedAt"`,
        [input.containerId, input.targetStatus],
      );
      if (input.targetStatus === 'gate_in') {
        await client.query(
          `UPDATE container_visits
              SET visit_status = 'active', started_at = COALESCE(started_at, now()), updated_at = now()
            WHERE id = (
              SELECT id FROM container_visits
               WHERE container_id = $1 AND visit_status IN ('planned', 'active')
               ORDER BY created_at DESC LIMIT 1
            )`,
          [input.containerId],
        );
      }
      if (input.targetStatus === 'gate_out') {
        await client.query(
          `UPDATE container_visits
              SET visit_status = 'completed', completed_at = COALESCE(completed_at, now()), updated_at = now()
            WHERE id = (
              SELECT id FROM container_visits
               WHERE container_id = $1 AND visit_status IN ('planned', 'active')
               ORDER BY COALESCE(started_at, created_at) DESC LIMIT 1
            )`,
          [input.containerId],
        );
      }
      await client.query(
        `INSERT INTO audit_logs (
           user_id, action, module, target_table, target_id, old_data, new_data, ip_address
         ) VALUES ($1, 'container_status_transition', 'container', 'containers', $2, $3::jsonb, $4::jsonb, $5)`,
        [input.userId, input.containerId, JSON.stringify({ status: input.fromStatus }),
         JSON.stringify({ status: input.toStatus }), input.ipAddress],
      );
      await client.query('COMMIT');
      return { updatedAt: updated.rows[0].updatedAt };
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

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
      conditions.push(`(
        c.container_no ILIKE $${values.length}
        OR c.seal_no ILIKE $${values.length}
        OR EXISTS (
          SELECT 1
            FROM container_visits sv
            LEFT JOIN eir_transactions se ON se.container_visit_id = sv.id
            LEFT JOIN eir_seals ss ON ss.eir_transaction_id = se.id
           WHERE sv.container_id = c.id
             AND (sv.visit_reference ILIKE $${values.length}
               OR sv.bl_booking_no ILIKE $${values.length}
               OR se.reference_no ILIKE $${values.length}
               OR se.bl_booking_no ILIKE $${values.length}
               OR se.plate_number ILIKE $${values.length}
               OR ss.seal_number ILIKE $${values.length}
               OR ss.raw_value ILIKE $${values.length})
        )
      )`);
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
    const visitResult = await query(
      `SELECT cv.id, cv.visit_reference AS "visitReference", cv.booking_id AS "bookingId",
              cv.vessel_call_id AS "vesselCallId", cv.visit_status AS status,
              cv.load_status AS "loadStatus", cv.cargo_type AS "cargoType",
              cv.gross_weight_kg AS "grossWeightKg", cv.bl_booking_no AS "blBookingNumber",
              cv.customer_name AS "customerName", cv.transport_company_name AS "transportCompanyName",
              cv.valid_to AS "validTo", cv.started_at AS "startedAt",
              cv.completed_at AS "completedAt", cv.created_at AS "createdAt"
         FROM container_visits cv
        WHERE cv.container_id = $1
        ORDER BY COALESCE(cv.started_at, cv.created_at) DESC, cv.created_at DESC`,
      [id],
    );
    const eirResult = await query(
      `SELECT e.id, e.container_visit_id AS "containerVisitId",
              e.reference_no AS "referenceNumber", e.activity, e.eir_status AS status,
              e.terminal_code AS "terminalCode", e.terminal_name AS "terminalName",
              e.operator_code AS "operatorCode", e.operator_name AS "operatorName",
              e.customer_name AS "customerName", e.location_code AS "locationCode",
              e.gate_label AS "gateLabel", e.lane_code AS "laneCode", e.valid_to AS "validTo",
              e.check_in_at AS "checkInAt", e.check_out_at AS "checkOutAt",
              e.bl_booking_no AS "blBookingNumber", e.load_status AS "loadStatus",
              e.iso_code AS "isoCode", e.container_type_description AS "containerTypeDescription",
              e.size_feet AS "sizeFeet", e.cargo_type_description AS "cargoTypeDescription",
              e.gross_weight_kg AS "grossWeightKg", e.temperature_c AS "temperatureC",
              e.ventilation_description AS "ventilationDescription", e.imdg_class AS "imdgClass",
              e.sound_damage_code AS "soundDamageCode", e.remark,
              e.vessel_name AS "vesselName", e.voyage_in AS "voyageIn", e.voyage_out AS "voyageOut",
              e.transport_company_name AS "transportCompanyName", e.plate_number AS "plateNumber",
              e.empty_return_place AS "emptyReturnPlace", e.issued_at AS "issuedAt"
         FROM eir_transactions e
         JOIN container_visits cv ON cv.id = e.container_visit_id
        WHERE cv.container_id = $1
        ORDER BY e.issued_at DESC, e.created_at DESC`,
      [id],
    );
    const sealResult = await query(
      `SELECT es.id, es.eir_transaction_id AS "eirTransactionId", es.sequence_no AS "sequenceNo",
              es.seal_number AS "sealNumber", es.raw_value AS "rawValue",
              es.seal_condition AS "sealCondition", es.notes
         FROM eir_seals es
         JOIN eir_transactions e ON e.id = es.eir_transaction_id
         JOIN container_visits cv ON cv.id = e.container_visit_id
        WHERE cv.container_id = $1
        ORDER BY e.issued_at DESC, es.sequence_no`,
      [id],
    );
    const damageResult = await query(
      `SELECT d.id, d.eir_transaction_id AS "eirTransactionId",
              d.condition_code AS "conditionCode", d.component_code AS "componentCode",
              d.damage_type AS "damageType", d.description,
              d.length_cm AS "lengthCm", d.width_cm AS "widthCm", d.height_cm AS "heightCm",
              d.severity, d.observed_at AS "observedAt"
         FROM eir_damage_observations d
         JOIN eir_transactions e ON e.id = d.eir_transaction_id
         JOIN container_visits cv ON cv.id = e.container_visit_id
        WHERE cv.container_id = $1
        ORDER BY d.observed_at DESC`,
      [id],
    );
    const mediaResult = await query(
      `SELECT m.id, m.eir_transaction_id AS "eirTransactionId", m.subject_type AS "subjectType",
              m.media_kind AS "mediaKind", m.capture_stage AS "captureStage",
              m.movement_direction AS "movementDirection", m.view_label AS "viewLabel",
              m.media_url AS "mediaUrl", m.storage_provider AS "storageProvider",
              m.bucket_name AS "bucketName", m.storage_key AS "storageKey", m.mime_type AS "mimeType",
              m.captured_at AS "capturedAt", m.detected_value AS "detectedValue",
              m.recognition_confidence AS "recognitionConfidence", m.is_primary AS "isPrimary"
         FROM eir_media m
         JOIN eir_transactions e ON e.id = m.eir_transaction_id
         JOIN container_visits cv ON cv.id = e.container_visit_id
        WHERE cv.container_id = $1
        ORDER BY m.captured_at DESC, m.sequence_no`,
      [id],
    );

    const sealsByEir = new Map<string, ContainerEirSeal[]>();
    for (const seal of sealResult.rows) {
      const eirId = String(seal.eirTransactionId);
      const item = { ...seal, sequenceNo: Number(seal.sequenceNo) } as ContainerEirSeal;
      sealsByEir.set(eirId, [...(sealsByEir.get(eirId) ?? []), item]);
    }
    const damagesByEir = new Map<string, ContainerEirDamage[]>();
    for (const damage of damageResult.rows) {
      const eirId = String(damage.eirTransactionId);
      const item = {
        ...damage,
        lengthCm: numberOrNull(damage.lengthCm),
        widthCm: numberOrNull(damage.widthCm),
        heightCm: numberOrNull(damage.heightCm),
      } as ContainerEirDamage;
      damagesByEir.set(eirId, [...(damagesByEir.get(eirId) ?? []), item]);
    }
    const mediaByEir = new Map<string, ContainerEirMedia[]>();
    for (const media of mediaResult.rows) {
      const eirId = String(media.eirTransactionId);
      const item = {
        ...media,
        recognitionConfidence: numberOrNull(media.recognitionConfidence),
      } as ContainerEirMedia;
      mediaByEir.set(eirId, [...(mediaByEir.get(eirId) ?? []), item]);
    }
    const eirsByVisit = new Map<string, ContainerEirTransaction[]>();
    for (const eir of eirResult.rows) {
      const eirId = String(eir.id);
      const visitId = String(eir.containerVisitId);
      const item = {
        ...eir,
        sizeFeet: numberOrNull(eir.sizeFeet),
        grossWeightKg: numberOrNull(eir.grossWeightKg),
        temperatureC: numberOrNull(eir.temperatureC),
        seals: sealsByEir.get(eirId) ?? [],
        damages: damagesByEir.get(eirId) ?? [],
        media: mediaByEir.get(eirId) ?? [],
      } as ContainerEirTransaction;
      eirsByVisit.set(visitId, [...(eirsByVisit.get(visitId) ?? []), item]);
    }
    const visits = visitResult.rows.map((visit) => ({
      ...visit,
      grossWeightKg: numberOrNull(visit.grossWeightKg),
      eirs: eirsByVisit.get(String(visit.id)) ?? [],
    })) as ContainerVisit[];

    return {
      ...mapListRow(row),
      containerType: mapType(typeResult.rows[0]),
      vesselCallCode: row.vesselCallCode == null ? null : String(row.vesselCallCode),
      bookings: bookingResult.rows as ContainerBookingSummary[],
      currentPosition: (positionResult.rows[0] as ContainerPositionSummary | undefined) ?? null,
      visits,
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
       dto.sealNumber ?? null, dto.cargoType ?? 'general', dto.status ?? 'expected', dto.grossWeightKg ?? null,
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

import { randomUUID } from 'crypto';
import { getClient, query } from '../../../infrastructure/database/db';
import { AppError } from '../../../shared/errors/app-error';
import {
  ContainerImportBatchSummary, ContainerIntakeResult, ContainerIntakeRow,
  ContainerIntakeSource,
} from '../application/container-intake.dto';

const compactReference = (value: string): string =>
  value.toUpperCase().replace(/[^A-Z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 45);

const visitReferenceFor = (row: ContainerIntakeRow): string => {
  const datePart = (row.expectedArrivalAt || row.requestedPickupDate || new Date().toISOString()).slice(0, 10).replace(/-/g, '');
  const sourcePart = row.sourceReference ? compactReference(row.sourceReference) : datePart;
  return `${row.sourceType === 'port_vessel' ? 'PORT' : 'TC'}-${row.containerNumber}-${sourcePart}`.slice(0, 100);
};

export class ContainerIntakeRepository {
  async createBatch(sourceType: ContainerIntakeSource, fileName: string, fileSize: number, userId: string): Promise<string> {
    const result = await query(
      `INSERT INTO container_import_batches (source_type, file_name, file_size_bytes, created_by)
       VALUES ($1, $2, $3, $4) RETURNING id`,
      [sourceType, fileName, fileSize, userId],
    );
    return String(result.rows[0].id);
  }

  async saveRowResult(batchId: string, result: ContainerIntakeResult, rawData: Record<string, unknown>): Promise<void> {
    await query(
      `INSERT INTO container_import_rows (
         import_batch_id, row_number, normalized_container_no, result_status,
         container_id, container_visit_id, raw_data, errors
       ) VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8::jsonb)`,
      [batchId, result.rowNumber, result.containerNumber, result.status,
       result.containerId ?? null, result.visitId ?? null, JSON.stringify(rawData), JSON.stringify(result.errors)],
    );
  }

  async completeBatch(batchId: string, results: ContainerIntakeResult[]): Promise<void> {
    const successRows = results.filter((row) => ['created_master', 'created_visit', 'updated_visit'].includes(row.status)).length;
    const duplicateRows = results.filter((row) => row.status === 'duplicate').length;
    const failedRows = results.filter((row) => row.status === 'rejected').length;
    const importStatus = failedRows === results.length ? 'failed' : failedRows > 0 ? 'partial' : 'completed';
    await query(
      `UPDATE container_import_batches
          SET import_status=$2, total_rows=$3, success_rows=$4, duplicate_rows=$5,
              failed_rows=$6, completed_at=now()
        WHERE id=$1`,
      [batchId, importStatus, results.length, successRows, duplicateRows, failedRows],
    );
  }

  async failBatch(batchId: string): Promise<void> {
    await query(
      `UPDATE container_import_batches
          SET import_status='failed', failed_rows=GREATEST(failed_rows, 1), completed_at=now()
        WHERE id=$1`,
      [batchId],
    );
  }

  async processRow(row: ContainerIntakeRow, userId: string, batchId?: string, rowNumber?: number): Promise<ContainerIntakeResult> {
    const client = await getClient();
    try {
      await client.query('BEGIN');
      const typeResult = await client.query(
        'SELECT id FROM container_types WHERE upper(code) = upper($1) LIMIT 1',
        [row.containerTypeCode],
      );
      if (!typeResult.rows[0]) throw new AppError(`Không tìm thấy loại Container '${row.containerTypeCode}'.`, 422, 'CONTAINER_TYPE_NOT_FOUND');
      const typeId = String(typeResult.rows[0].id);

      let vesselCallId: string | null = null;
      if (row.vesselCallCode) {
        const vesselResult = await client.query('SELECT id FROM vessel_calls WHERE upper(call_code)=upper($1) LIMIT 1', [row.vesselCallCode]);
        if (!vesselResult.rows[0]) throw new AppError(`Không tìm thấy chuyến tàu '${row.vesselCallCode}'.`, 422, 'VESSEL_CALL_NOT_FOUND');
        vesselCallId = String(vesselResult.rows[0].id);
      }

      const inserted = await client.query(
        `INSERT INTO containers (
           container_type_id, container_no, cargo_type, status, seal_no, gross_weight_kg,
           is_reefer, is_dangerous, is_perishable, is_oversized
         ) SELECT $1,$2,$3::cargo_type,'expected',$4,$5,
                  ct.category='reefer' OR $3::text='reefer', $3::text='dangerous', $3::text='perishable', $3::text='oversized'
             FROM container_types ct WHERE ct.id=$1
         ON CONFLICT (container_no) DO NOTHING RETURNING id`,
        [typeId, row.containerNumber, row.cargoType ?? 'general', row.sealNumber ?? null, row.grossWeightKg ?? null],
      );
      const createdMaster = Boolean(inserted.rows[0]);
      const containerResult = createdMaster ? inserted : await client.query(
        'SELECT id, container_type_id AS "containerTypeId" FROM containers WHERE container_no=$1 FOR UPDATE',
        [row.containerNumber],
      );
      const container = containerResult.rows[0];
      if (!container) throw new AppError('Không thể tạo hoặc tìm Container.', 500, 'CONTAINER_INTAKE_FAILED');
      if (!createdMaster && String(container.containerTypeId) !== typeId) {
        throw new AppError(`Container '${row.containerNumber}' đã tồn tại nhưng khác loại Container.`, 409, 'CONTAINER_TYPE_MISMATCH');
      }

      const visitReference = visitReferenceFor(row);
      const existingVisit = await client.query(
        'SELECT id FROM container_visits WHERE visit_reference=$1 FOR UPDATE',
        [visitReference],
      );
      let visitId: string;
      let status: ContainerIntakeResult['status'];
      if (existingVisit.rows[0]) {
        visitId = String(existingVisit.rows[0].id);
        const updated = await client.query(
          `UPDATE container_visits SET
             source_reference=$2, seal_number=$3, load_status=$4, cargo_type=$5,
             gross_weight_kg=$6, vessel_call_id=$7, expected_arrival_at=$8,
             expected_available_at=$9, requested_pickup_date=$10, bl_booking_no=$11,
             customer_name=$12, transport_company_name=$13,
             data_status=$14, updated_at=now()
           WHERE id=$1 AND (
             source_reference IS DISTINCT FROM $2 OR seal_number IS DISTINCT FROM $3 OR
             load_status IS DISTINCT FROM $4 OR cargo_type IS DISTINCT FROM $5::cargo_type OR
             gross_weight_kg IS DISTINCT FROM $6 OR vessel_call_id IS DISTINCT FROM $7 OR
             expected_arrival_at IS DISTINCT FROM $8 OR expected_available_at IS DISTINCT FROM $9 OR
             requested_pickup_date IS DISTINCT FROM $10 OR bl_booking_no IS DISTINCT FROM $11 OR
             customer_name IS DISTINCT FROM $12 OR transport_company_name IS DISTINCT FROM $13 OR
             data_status IS DISTINCT FROM $14
           ) RETURNING id`,
          [visitId, row.sourceReference ?? null, row.sealNumber ?? null, row.loadStatus ?? 'unknown', row.cargoType ?? 'general',
           row.grossWeightKg ?? null, vesselCallId, row.expectedArrivalAt ?? null, row.expectedAvailableAt ?? null,
           row.requestedPickupDate ?? null, row.blBookingNumber ?? null, row.customerName ?? null,
           row.transportCompanyName ?? null, row.sourceType === 'port_vessel' ? 'verified' : 'pending_verification'],
        );
        status = updated.rows[0] ? 'updated_visit' : 'duplicate';
      } else {
        visitId = randomUUID();
        await client.query(
          `INSERT INTO container_visits (
             id, visit_reference, container_id, vessel_call_id, visit_status, load_status,
             cargo_type, gross_weight_kg, bl_booking_no, customer_name, transport_company_name,
             source_type, movement_type, data_status, source_reference, seal_number,
             expected_arrival_at, expected_available_at, requested_pickup_date,
             declared_by, import_batch_id, source_row_number
           ) VALUES ($1,$2,$3,$4,'planned',$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21)`,
          [visitId, visitReference, container.id, vesselCallId, row.loadStatus ?? 'unknown', row.cargoType ?? 'general',
           row.grossWeightKg ?? null, row.blBookingNumber ?? null, row.customerName ?? null,
           row.transportCompanyName ?? null, row.sourceType, row.movementType,
           row.sourceType === 'port_vessel' ? 'verified' : 'pending_verification', row.sourceReference ?? null,
           row.sealNumber ?? null, row.expectedArrivalAt ?? null, row.expectedAvailableAt ?? null,
           row.requestedPickupDate ?? null, userId, batchId ?? null, rowNumber ?? null],
        );
        status = createdMaster ? 'created_master' : 'created_visit';
      }
      await client.query('COMMIT');
      return { rowNumber: rowNumber ?? 1, containerNumber: row.containerNumber, status, containerId: String(container.id), visitId, errors: [] };
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async listBatches(limit = 30): Promise<ContainerImportBatchSummary[]> {
    const result = await query(
      `SELECT id, source_type AS "sourceType", file_name AS "fileName",
              import_status AS "importStatus", total_rows AS "totalRows",
              success_rows AS "successRows", duplicate_rows AS "duplicateRows",
              failed_rows AS "failedRows", created_at AS "createdAt", completed_at AS "completedAt"
         FROM container_import_batches ORDER BY created_at DESC LIMIT $1`,
      [limit],
    );
    return result.rows as ContainerImportBatchSummary[];
  }
}

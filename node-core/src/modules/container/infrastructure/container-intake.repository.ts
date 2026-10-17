import { randomUUID } from 'crypto';
import { getClient, query } from '../../../infrastructure/database/db';
import { AppError } from '../../../shared/errors/app-error';
import { ContainerImportBatchSummary, ContainerIntakeResult, ContainerIntakeRow, ContainerIntakeSource, TransportContainerDeclaration, } from '../application/container-intake.dto';
export class ContainerIntakeRepository {
    async listContainerTypes() {
        return (await query('SELECT code, description FROM container_types ORDER BY size, code')).rows;
    }
    async createBatch(sourceType: ContainerIntakeSource, fileName: string, fileSize: number, userId: string): Promise<string> {
        const result = await query(`INSERT INTO container_import_batches (source_type, file_name, file_size_bytes, created_by)
       VALUES ($1, $2, $3, $4) RETURNING id`, [sourceType, fileName, fileSize, userId]);
        return String(result.rows[0].id);
    }
    async saveRowResult(batchId: string, result: ContainerIntakeResult, rawData: Record<string, unknown>): Promise<void> {
        await query(`INSERT INTO container_import_rows (
         import_batch_id, row_number, normalized_container_no, result_status,
         container_id, container_visit_id, raw_data, errors
       ) VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8::jsonb)`, [batchId, result.rowNumber, result.containerNumber, result.status,
            result.containerId ?? null, result.visitId ?? null, JSON.stringify(rawData), JSON.stringify(result.errors)]);
    }
    async completeBatch(batchId: string, results: ContainerIntakeResult[]): Promise<void> {
        const successRows = results.filter((row) => ['created_master', 'created_visit', 'updated_visit'].includes(row.status)).length;
        const duplicateRows = results.filter((row) => row.status === 'duplicate').length;
        const failedRows = results.filter((row) => row.status === 'rejected').length;
        const importStatus = failedRows === results.length ? 'failed' : failedRows > 0 ? 'partial' : 'completed';
        await query(`UPDATE container_import_batches
          SET import_status=$2, total_rows=$3, success_rows=$4, duplicate_rows=$5,
              failed_rows=$6, completed_at=now()
        WHERE id=$1`, [batchId, importStatus, results.length, successRows, duplicateRows, failedRows]);
    }
    async failBatch(batchId: string): Promise<void> {
        await query(`UPDATE container_import_batches
          SET import_status='failed', failed_rows=GREATEST(failed_rows, 1), completed_at=now()
        WHERE id=$1`, [batchId]);
    }
    async listCargoTypes(): Promise<Array<{
        code: string;
        name: string;
        requiresPower: boolean;
    }>> {
        const result = await query('SELECT code, name_vi AS name, requires_power AS "requiresPower" FROM container_cargo_types ORDER BY sort_order');
        return result.rows;
    }
    async processRow(row: ContainerIntakeRow, userId: string, batchId?: string, rowNumber?: number): Promise<ContainerIntakeResult> {
        const client = await getClient();
        try {
            await client.query('BEGIN');
            const types = await client.query('SELECT id, category, max_gross_weight_kg FROM container_types WHERE code=$1', [row.containerTypeCode]);
            const type = types.rows[0];
            if (!type)
                throw new AppError('Loại Container không tồn tại trong danh mục.', 422, 'CONTAINER_TYPE_NOT_FOUND');
            if (row.cargoType === 'reefer' && type.category !== 'reefer')
                throw new AppError('Hàng đông lạnh phải dùng Container lạnh.', 422, 'REEFER_TYPE_REQUIRED');
            if (type.max_gross_weight_kg != null && row.grossWeightKg != null && row.grossWeightKg > Number(type.max_gross_weight_kg))
                throw new AppError('Khối lượng vượt giới hạn của loại Container.', 422, 'CONTAINER_WEIGHT_EXCEEDED');
            const inserted = await client.query(`INSERT INTO containers(container_type_id,container_no,cargo_type,status,gross_weight_kg,required_power_kw,is_reefer,is_dangerous,is_perishable,is_oversized)
         VALUES($1,$2,$3,'expected',$4,$5,$6,$7,$8,$9) ON CONFLICT(container_no) DO NOTHING RETURNING id`, [type.id, row.containerNumber, row.cargoType, row.grossWeightKg ?? null, row.requiredPowerKw ?? null, type.category === 'reefer', row.cargoType === 'dangerous', row.cargoType === 'perishable', row.cargoType === 'oversized']);
            const createdMaster = Boolean(inserted.rows[0]);
            const locked = await client.query('SELECT id,container_type_id,status FROM containers WHERE container_no=$1 FOR UPDATE', [row.containerNumber]);
            const container = locked.rows[0];
            if (String(container.container_type_id) !== String(type.id))
                throw new AppError('Container đã tồn tại với loại khác.', 409, 'CONTAINER_TYPE_MISMATCH');
            const existing = await client.query(`SELECT id, declared_by, visit_status, booking_id FROM container_visits WHERE container_id=$1
         AND visit_status IN ('planned','active') ORDER BY created_at DESC LIMIT 1 FOR UPDATE`, [container.id]);
            const visit = existing.rows[0];
            if (!visit && ['gate_in', 'in_yard', 'reserved', 'moving', 'discharged', 'damaged'].includes(container.status)) {
                throw new AppError('Container đang được vận hành trong kho bãi. Không thể tạo khai báo mới.', 409, 'DECLARATION_IN_USE');
            }
            if (visit && String(visit.declared_by) !== userId)
                throw new AppError('Container đang có lượt khai báo của tài khoản khác.', 409, 'CONTAINER_DECLARATION_CONFLICT');
            const booked = await client.query(`SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id WHERE bc.container_id=$1 AND lower(b.status::text) NOT IN ('completed','canceled','cancelled','rejected','expired') LIMIT 1`, [container.id]);
            if (booked.rowCount || (visit && (visit.visit_status !== 'planned' || visit.booking_id)))
                throw new AppError('Container đã có Booking hoặc đang trong bãi. Không thể sửa khai báo.', 409, 'DECLARATION_IN_USE');
            let visitId: string;
            let status: ContainerIntakeResult['status'];
            const values = [row.loadStatus, row.cargoType, row.grossWeightKg ?? null, row.requiredPowerKw ?? null, row.customerName ?? null, row.sealNumber ?? null];
            if (visit) {
                visitId = String(visit.id);
                const updated = await client.query(`UPDATE container_visits SET load_status=$2,cargo_type=$3,gross_weight_kg=$4,required_power_kw=$5,customer_name=$6,seal_number=$7,updated_at=now()
           WHERE id=$1 AND (load_status,cargo_type,gross_weight_kg,required_power_kw,customer_name,seal_number)
            IS DISTINCT FROM ($2::varchar,$3::cargo_type,$4::numeric,$5::numeric,$6::varchar,$7::varchar) RETURNING id`, [visitId, ...values]);
                status = updated.rowCount ? 'updated_visit' : 'duplicate';
            }
            else {
                visitId = randomUUID();
                await client.query(`INSERT INTO container_visits(id,visit_reference,container_id,load_status,cargo_type,gross_weight_kg,required_power_kw,customer_name,seal_number,source_type,declared_by,import_batch_id,source_row_number,data_status)
          VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,'transport_company',$10,$11,$12,NULL)`, [visitId, 'YARD-' + visitId, container.id, ...values, userId, batchId ?? null, rowNumber ?? null]);
                status = createdMaster ? 'created_master' : 'created_visit';
            }
            // Booking and inventory read the physical master; persist the same cargo/power there.
            await client.query(`UPDATE containers SET cargo_type=$2,gross_weight_kg=$3,required_power_kw=$4,is_reefer=$5,is_dangerous=$6,is_perishable=$7,is_oversized=$8,seal_no=$10,status=CASE WHEN $9::boolean THEN 'expected'::container_status ELSE status END,arrived_at=CASE WHEN $9::boolean THEN NULL ELSE arrived_at END,left_at=CASE WHEN $9::boolean THEN NULL ELSE left_at END,updated_at=now() WHERE id=$1`, [container.id, row.cargoType, row.grossWeightKg ?? null, row.requiredPowerKw ?? null, type.category === 'reefer', row.cargoType === 'dangerous', row.cargoType === 'perishable', row.cargoType === 'oversized', !visit, row.sealNumber ?? null]);
            await client.query('COMMIT');
            return { rowNumber: rowNumber ?? 1, containerNumber: row.containerNumber, status, containerId: String(container.id), visitId, errors: [] };
        }
        catch (error) {
            await client.query('ROLLBACK');
            throw error;
        }
        finally {
            client.release();
        }
    }
    async listBatches(limit = 30): Promise<ContainerImportBatchSummary[]> {
        const result = await query(`SELECT id, source_type AS "sourceType", file_name AS "fileName",
              import_status AS "importStatus", total_rows AS "totalRows",
              success_rows AS "successRows", duplicate_rows AS "duplicateRows",
              failed_rows AS "failedRows", created_at AS "createdAt", completed_at AS "completedAt"
         FROM container_import_batches ORDER BY created_at DESC LIMIT $1`, [limit]);
        return result.rows as ContainerImportBatchSummary[];
    }
    async listTransportDeclarations(userId: string): Promise<TransportContainerDeclaration[]> {
        const result = await query(`SELECT cv.id, c.id AS "containerId", c.container_no AS "containerNumber",
              ct.code AS "containerTypeCode", ct.description AS "containerTypeDescription",
              cv.seal_number AS "sealNumber", cv.load_status AS "loadStatus", cv.cargo_type AS "cargoType", cargo.name_vi AS "cargoTypeName",
              cv.gross_weight_kg::float8 AS "grossWeightKg", cv.required_power_kw::float8 AS "requiredPowerKw",
              cv.customer_name AS "customerName",
              cv.created_at AS "createdAt", cv.updated_at AS "updatedAt"
         FROM container_visits cv JOIN containers c ON c.id=cv.container_id
         JOIN container_types ct ON ct.id=c.container_type_id
         LEFT JOIN container_cargo_types cargo ON cargo.code=cv.cargo_type
        WHERE cv.source_type='transport_company' AND cv.declared_by=$1 ORDER BY cv.created_at DESC`, [userId]);
        return result.rows as TransportContainerDeclaration[];
    }
}

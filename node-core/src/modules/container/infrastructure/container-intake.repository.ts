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
            const inserted = await client.query(`INSERT INTO containers(container_type_id,container_no,status,is_reefer)
              VALUES($1,$2,'expected',$3) ON CONFLICT(container_no) DO NOTHING RETURNING id`, [type.id,row.containerNumber,type.category==='reefer']);
            const createdMaster = Boolean(inserted.rows[0]);
            const container = (await client.query('SELECT id,container_type_id FROM containers WHERE container_no=$1 FOR UPDATE',[row.containerNumber])).rows[0];
            if (String(container.container_type_id)!==String(type.id))
              throw new AppError('Container đã tồn tại với loại khác. Hãy dùng loại đã đăng ký.',409,'CONTAINER_TYPE_MISMATCH');
            const memberships = await client.query('SELECT carrier_id FROM carrier_users WHERE user_id=$1',[userId]);
            if(memberships.rows.length!==1) throw new AppError('Tài khoản cần thuộc đúng một công ty vận tải để khai báo.',403,'CARRIER_MEMBERSHIP_REQUIRED');
            const values=[container.id,userId,memberships.rows[0].carrier_id,row.loadStatus,row.cargoType,row.grossWeightKg??null,row.requiredPowerKw??null,row.customerName??null,row.sealNumber??null];
            const duplicate=await client.query(`SELECT d.id FROM container_declarations d WHERE
              (container_id,declared_by,carrier_id,load_status,cargo_type,gross_weight_kg,required_power_kw,customer_name,seal_number)
              IS NOT DISTINCT FROM ($1::uuid,$2::uuid,$3::uuid,$4::varchar,$5::cargo_type,$6::numeric,$7::numeric,$8::varchar,$9::varchar)
              AND NOT EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.declaration_id=d.id) LIMIT 1`,values);
            const declarationId=duplicate.rows[0]?.id??randomUUID();
            if(!duplicate.rows[0]) await client.query(`INSERT INTO container_declarations(id,container_id,declared_by,carrier_id,load_status,cargo_type,gross_weight_kg,required_power_kw,customer_name,seal_number,import_batch_id,source_row_number)
              VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)`,[declarationId,...values,batchId??null,rowNumber??null]);
            const status: ContainerIntakeResult['status']=duplicate.rows[0]?'duplicate':createdMaster?'created_master':'created_visit';
            await client.query('COMMIT');
            return { rowNumber: rowNumber ?? 1, containerNumber: row.containerNumber, status, containerId: String(container.id), declarationId: String(declarationId), errors: [] };
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
    async listTransportDeclarations(userId: string, containerNumber = ''): Promise<TransportContainerDeclaration[]> {
        const result = await query(`SELECT cv.id, EXISTS(SELECT 1 FROM booking_containers bc WHERE bc.declaration_id=cv.id) AS "isBooked", c.id AS "containerId", c.container_no AS "containerNumber",
              ct.code AS "containerTypeCode", ct.description AS "containerTypeDescription",
              cv.seal_number AS "sealNumber", cv.load_status AS "loadStatus", cv.cargo_type AS "cargoType", cargo.name_vi AS "cargoTypeName",
              cv.gross_weight_kg::float8 AS "grossWeightKg", cv.required_power_kw::float8 AS "requiredPowerKw",
              cv.customer_name AS "customerName",
              cv.created_at AS "createdAt", cv.updated_at AS "updatedAt",
              COALESCE((SELECT json_agg(json_build_object('id',b.id,'bookingCode',b.booking_code,
                'bookingType',b.booking_type,'status',b.status,
                'isDeclarationBooking',bc.declaration_id=cv.id) ORDER BY b.appointment_start,b.id)
                FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
                WHERE bc.container_id=cv.container_id
                  AND (bc.declaration_id=cv.id OR bc.container_visit_id IN (
                    SELECT original.container_visit_id FROM booking_containers original
                    WHERE original.declaration_id=cv.id AND original.container_visit_id IS NOT NULL))
                  AND EXISTS(SELECT 1 FROM carrier_users member
                    WHERE member.user_id=$1 AND member.carrier_id=b.carrier_id)), '[]'::json) AS bookings
         FROM container_declarations cv JOIN containers c ON c.id=cv.container_id
         JOIN container_types ct ON ct.id=c.container_type_id
         LEFT JOIN container_cargo_types cargo ON cargo.code=cv.cargo_type
        WHERE (cv.declared_by=$1 OR EXISTS(SELECT 1 FROM carrier_users cu WHERE cu.user_id=$1 AND cu.carrier_id=cv.carrier_id)) AND c.container_no LIKE $2 ORDER BY cv.created_at DESC LIMIT 50`, [userId,containerNumber.toUpperCase()+'%']);
        return result.rows as TransportContainerDeclaration[];
    }
}

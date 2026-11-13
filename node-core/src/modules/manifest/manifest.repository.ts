import { randomUUID } from 'crypto';
import { isDeepStrictEqual } from 'util';
import { getClient, query } from '../../infrastructure/database/db';
import { ConflictError, NotFoundError, ValidationError } from '../../shared/errors/app-error';
import { ManifestInput } from './manifest.schema';

export function buildManifest(input: ManifestInput, id: string, manifestNumber: string, createdAt = new Date().toISOString()) {
  const records = input.bills.flatMap(b => b.containers.map(c => ({
    ...c, id: randomUUID(), blNumber: b.blNumber, shipper: b.shipper, consignee: b.consignee,
    cargoType: c.isReefer ? 'REEFER' : 'GENERAL', isIsoValid: true, status: 'VALID',
  })));
  return {
    ...input, id, manifestNumber, createdAt, source: 'NODE_CORE', version: 1,
    status: input.isDraft ? 'DRAFT' : 'VALIDATED', submittedAt: new Date().toISOString(),
    sentToPortAt: null as string | null, voyageInfo: { voyageNumber: input.voyageNumber, vesselName: input.vesselName },
    totalContainers: records.length, totalWeightKg: records.reduce((sum, c) => sum + c.grossWeightKg, 0),
    exceptionsCount: 0, validContainersCount: records.length, exceptionContainersCount: 0, records,
    blGroups: input.bills.map(b => ({ ...b,
      cargoDescription: [...new Set(b.containers.map(c => c.commodity))].join('; '),
      grossWeightKg: b.containers.reduce((sum, c) => sum + c.grossWeightKg, 0),
      containers: records.filter(c => c.blNumber === b.blNumber),
    })),
  };
}

export type StoredManifest = ReturnType<typeof buildManifest> & { revision: number };
export type ManifestSummary = Pick<StoredManifest, 'id' | 'manifestNumber' | 'voyageNumber' | 'vesselName' | 'direction' | 'status' | 'source' | 'createdAt' | 'totalContainers' | 'sentToPortAt' | 'revision'> & { totalBills: number };
const registeredEquipment = `SELECT c.id AS "containerId",c.container_no AS "containerNumber",
  ct.code AS "isoType",c.iso_size_type AS "isoSizeType",c.is_reefer AS "isReefer"
  FROM containers c JOIN container_types ct ON ct.id=c.container_type_id`;
export interface RegisteredEquipment { containerId: string; containerNumber: string; isoType: string; isoSizeType: string | null; isReefer: boolean }

export class ManifestRepository {
  async lookupContainers(numbers: string[]): Promise<RegisteredEquipment[]> {
    return (await query(`${registeredEquipment} WHERE c.container_no=ANY($1::text[]) AND NOT c.identity_quarantined`, [numbers])).rows;
  }
  async usedBillNumbers(owner: string, numbers: string[]): Promise<string[]> {
    return (await query('SELECT bl_number FROM shipping_manifest_bills WHERE owner_id=$1 AND bl_number=ANY($2::text[])', [owner, numbers])).rows.map(r => r.bl_number);
  }
  async lookupContainer(number: string): Promise<RegisteredEquipment | null> {
    return (await query(`${registeredEquipment} WHERE c.container_no=$1 AND NOT c.identity_quarantined`, [number])).rows[0] ?? null;
  }
  async list(owner: string, direction?: string): Promise<ManifestSummary[]> {
    return (await query(`SELECT jsonb_build_object(
      'id',id,'manifestNumber',payload->>'manifestNumber','voyageNumber',payload->>'voyageNumber',
      'vesselName',payload->>'vesselName','direction',payload->>'direction','status',payload->>'status',
      'source','NODE_CORE','createdAt',created_at,'sentToPortAt',payload->'sentToPortAt','revision',revision,
      'totalContainers',payload->'totalContainers','totalBills',jsonb_array_length(COALESCE(payload->'bills','[]'::jsonb))) AS document
      FROM shipping_manifests WHERE owner_id=$1 AND ($2::text IS NULL OR payload->>'direction'=$2) ORDER BY created_at DESC`, [owner, direction || null])).rows.map(r => r.document);
  }
  async detail(owner: string, id: string): Promise<StoredManifest> {
    const result = await query(`SELECT payload || jsonb_build_object('revision',revision,'createdAt',created_at) AS document FROM shipping_manifests WHERE owner_id=$1 AND id=$2`, [owner, id]);
    if (!result.rows[0]) throw new NotFoundError('Bản khai');
    return result.rows[0].document;
  }
  async save(owner: string, input: ManifestInput, existingId?: string): Promise<StoredManifest> {
    const client = await getClient();
    try {
      await client.query('BEGIN');
      // Serialize identical retries; the unique bill index protects concurrent manifests.
      await client.query('SELECT pg_advisory_xact_lock(hashtext($1))', [owner + input.requestKey]);
      const numbers = input.bills.flatMap(b => b.containers.map(c => c.containerNumber));
      const equipment = (await client.query(`${registeredEquipment} WHERE c.container_no=ANY($1::text[]) AND NOT c.identity_quarantined FOR SHARE OF c,ct`, [numbers])).rows as RegisteredEquipment[];
      const byNumber = new Map(equipment.map(c => [c.containerNumber, c]));
      const missing = numbers.filter(number => !byNumber.has(number));
      if (missing.length) throw new ValidationError(`Container không tồn tại trong hệ thống: ${missing.join(', ')}. Vui lòng khai báo container trước.`);
      input = { ...input, bills: input.bills.map(b => ({ ...b, containers: b.containers.map(c => ({ ...c, ...byNumber.get(c.containerNumber)! })) })) };
      const existing = await client.query('SELECT * FROM shipping_manifests WHERE owner_id=$1 AND request_key=$2 FOR UPDATE', [owner, input.requestKey]);
      const row = existing.rows[0] as { id: string; payload: ReturnType<typeof buildManifest>; revision: number; created_at: Date } | undefined;
      if (!existingId && row) {
        const comparable = (value: ManifestInput) => [value.voyageNumber, value.vesselName, value.direction, value.isDraft, value.bills];
        if (!isDeepStrictEqual(comparable(row.payload), comparable(input))) throw new ConflictError('Yêu cầu trước đã được lưu với nội dung khác. Vui lòng tải lại danh sách bản khai.');
        await client.query('COMMIT');
        return { ...row.payload, createdAt: new Date(row.created_at).toISOString(), revision: row.revision };
      }
      if (existingId && (!row || row.id !== existingId)) throw new NotFoundError('Bản khai');
      if (row && (row.payload.status !== 'DRAFT' || row.revision !== input.revision)) throw new ConflictError('Bản khai đã thay đổi hoặc đã hoàn tất. Vui lòng tải lại.');
      const id = row?.id || randomUUID();
      const document = buildManifest(input, id, row?.payload.manifestNumber || `MNF-${new Date().getUTCFullYear()}-${id.slice(0, 8).toUpperCase()}`, row ? new Date(row.created_at).toISOString() : undefined);
      const revision = (row?.revision || 0) + 1;
      if (row) {
        await client.query('UPDATE shipping_manifests SET payload=$1,revision=$2 WHERE id=$3 AND owner_id=$4', [document, revision, id, owner]);
        await client.query('DELETE FROM shipping_manifest_bills WHERE manifest_id=$1 AND owner_id=$2', [id, owner]);
      } else {
        await client.query('INSERT INTO shipping_manifests(id,owner_id,request_key,payload,created_at) VALUES($1,$2,$3,$4,$5)', [id, owner, input.requestKey, document, document.createdAt]);
      }
      for (const b of input.bills) await client.query('INSERT INTO shipping_manifest_bills(manifest_id,owner_id,bl_number) VALUES($1,$2,$3)', [id, owner, b.blNumber]);
      await client.query('COMMIT');
      return { ...document, revision };
    } catch (error) {
      await client.query('ROLLBACK');
      if ((error as { code?: string }).code === '23505') throw new ConflictError('Số vận đơn đã được sử dụng trong bản khai khác.');
      throw error;
    } finally { client.release(); }
  }
  async send(owner: string, id: string) {
    const result = await query(`UPDATE shipping_manifests SET
      payload=jsonb_set(jsonb_set(payload,'{status}','"SENT_TO_PORT"'),'{sentToPortAt}',COALESCE(NULLIF(payload->'sentToPortAt','null'::jsonb),to_jsonb(now()))),revision=revision+1
      WHERE owner_id=$1 AND id=$2 AND payload->>'status' IN ('VALIDATED','SENT_TO_PORT') RETURNING payload`, [owner, id]);
    if (!result.rows[0]) throw new ConflictError('Chỉ gửi được bản khai đã hoàn tất.');
    return { sentToPortAt: result.rows[0].payload.sentToPortAt, summary: { totalContainers: result.rows[0].payload.totalContainers, matchedCount: 0, pendingPortCount: result.rows[0].payload.totalContainers, portReceivedCount: 0, mismatchedCount: 0, matchPercentage: 0 }, reconciliationDetails: [] };
  }
  async recall(owner: string, id: string) {
    const result = await query(`UPDATE shipping_manifests SET payload=jsonb_set(jsonb_set(payload,'{status}','"VALIDATED"'),'{sentToPortAt}','null'),revision=revision+1
      WHERE owner_id=$1 AND id=$2 AND payload->>'status'='SENT_TO_PORT' RETURNING id`, [owner, id]);
    if (!result.rows[0]) throw new ConflictError('Bản khai chưa được gửi hoặc không còn khả dụng.');
  }
}

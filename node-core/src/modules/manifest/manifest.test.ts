import { randomUUID } from 'crypto';
import { manifestSchema } from './manifest.schema';
import { ManifestRepository, buildManifest } from './manifest.repository';
import pool, { query } from '../../infrastructure/database/db';
import express from 'express';
import { createServer, Server } from 'http';
import jwt from 'jsonwebtoken';
import { createManifestRouter } from './manifest.controller';
import { errorHandler } from '../../api/middleware/error.middleware';
import { isValidIso6346 } from '../container/application/container.validator';

const input = () => ({ requestKey: randomUUID(), voyageNumber: 'TEST-001', vesselName: 'Test Vessel', direction: 'IMPORT' as const, isDraft: false,
  bills: [{ blNumber: 'TEST-BL', shipper: 'Shipper', consignee: 'Consignee', containers: [
    { containerNumber: 'CSQU3054383', isoType: '40RF', sealNumber: 'SEAL-1', grossWeightKg: 23000, commodity: 'Cá đông lạnh', isReefer: false },
  ] }],
});
describe('Manifest cargo validation', () => {
  it('keeps cargo descriptions and identifies reefers without requiring or inventing temperature', () => {
    const parsed = manifestSchema.parse(input());
    const doc = buildManifest(parsed, randomUUID(), 'MNF-TEST');
    expect(doc.records[0]).toMatchObject({ commodity: 'Cá đông lạnh', isReefer: true, cargoType: 'REEFER' });
    expect(doc.records[0]).not.toHaveProperty('tempRequired');
    expect(doc.blGroups[0].containers).toEqual(doc.records);
  });
  it('rejects duplicate containers across different bills, not just within a bill', () => {
    const dto = input(); dto.bills.push({ ...dto.bills[0], blNumber: 'SECOND-BL' });
    expect(manifestSchema.safeParse(dto).success).toBe(false);
  });
  it.each([
    { containerNumber: 'CSQU3054384' }, { commodity: ' ' }, { grossWeightKg: -1 }, { grossWeightKg: 36001 }, { sealNumber: '' },
  ])('rejects incomplete or invalid cargo %j', patch => {
    const dto = input(); Object.assign(dto.bills[0].containers[0], patch);
    expect(manifestSchema.safeParse(dto).success).toBe(false);
  });
});

const local = process.env.DATABASE_URL === 'postgresql://postgres@127.0.0.1:55443/manifest_test';
(local ? describe : describe.skip)('Manifest atomic persistence in isolated PostgreSQL', () => {
  const owner = randomUUID(), other = randomUUID();
  const coldType = randomUUID(), dryType = randomUUID(), coldContainer = randomUUID(), dryContainer = randomUUID();
  const repo = new ManifestRepository();
  let server: Server, base: string;
  const oldSecret = process.env.JWT_SECRET;
  beforeAll(async () => {
    await query(`INSERT INTO container_types(id,code,size,category) VALUES($1,'40RF','ft40','reefer'),($2,'20GP','ft20','dry') ON CONFLICT(code) DO NOTHING`, [coldType,dryType]);
    await query(`INSERT INTO containers(id,container_type_id,container_no,is_reefer,iso_size_type)
      VALUES($1,(SELECT id FROM container_types WHERE code='40RF'),'CSQU3054383',true,'45R1'),
      ($2,(SELECT id FROM container_types WHERE code='20GP'),'MSCU6639870',false,'22G1')`, [coldContainer,dryContainer]);
    for (const id of [owner, other]) await query(`INSERT INTO users(id,username,email,password,role,is_active,created_at,updated_at)
      VALUES($1,$2,$3,'test-only','Shipping Line',true,now(),now())`, [id, 'manifest-'+id, id+'@example.invalid']);
    process.env.JWT_SECRET = 'manifest-local-test-secret';
    const app = express(); app.use(express.json()); app.use('/manifests', createManifestRouter()); app.use(errorHandler);
    server = createServer(app);
    await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
    base = `http://127.0.0.1:${(server.address() as { port: number }).port}/manifests`;
  });
  afterAll(async () => {
    await new Promise<void>((resolve, reject) => server.close(e => e ? reject(e) : resolve()));
    if (oldSecret === undefined) delete process.env.JWT_SECRET; else process.env.JWT_SECRET = oldSecret;
    await query('DELETE FROM shipping_manifests WHERE owner_id=ANY($1::uuid[])', [[owner, other]]);
    await query('DELETE FROM containers WHERE id=ANY($1::uuid[])', [[coldContainer,dryContainer]]);
    await query('DELETE FROM container_types WHERE id=ANY($1::uuid[])', [[coldType,dryType]]);
    await query('DELETE FROM users WHERE id=ANY($1::uuid[])', [[owner, other]]);
    await pool.end();
  });
  it('saves nested cargo once for simultaneous retries and hides it from other accounts', async () => {
    const dto = manifestSchema.parse(input());
    const [a,b] = await Promise.all([repo.save(owner, dto), repo.save(owner, dto)]);
    expect(a.id).toBe(b.id);
    const summary = (await repo.list(owner))[0];
    expect(summary).toMatchObject({id:a.id,totalBills:1,totalContainers:1});
    expect(summary).not.toHaveProperty('records');
    expect(summary).not.toHaveProperty('bills');
    expect(summary).not.toHaveProperty('blGroups');
    expect(new Date(summary.createdAt).getTime()).toBe(new Date(a.createdAt).getTime());
    expect((await new ManifestRepository().detail(owner, a.id)).records[0].commodity).toBe('Cá đông lạnh');
    await expect(repo.detail(other, a.id)).rejects.toThrow();
    expect(await repo.list(other)).toEqual([]);
  });
  it('rolls back the whole manifest when one bill conflicts', async () => {
    const dto = input(); dto.bills.unshift({ ...dto.bills[0], blNumber: 'UNUSED-BL', containers: [{ ...dto.bills[0].containers[0], containerNumber: 'MSCU6639870' }] });
    await expect(repo.save(owner, manifestSchema.parse(dto))).rejects.toThrow('Số vận đơn');
    expect((await repo.list(owner)).length).toBe(1);
    expect((await query("SELECT 1 FROM shipping_manifest_bills WHERE owner_id=$1 AND bl_number='UNUSED-BL'", [owner])).rowCount).toBe(0);
  });
  it('allows the same external BL number in another account', async () => {
    expect((await repo.save(other, manifestSchema.parse(input()))).bills[0].blNumber).toBe('TEST-BL');
  });
  it('updates drafts with revision protection, finishes them, and persists send/recall', async () => {
    const draft = input(); draft.isDraft = true; draft.bills[0].blNumber = 'DRAFT-BL';
    const dto = manifestSchema.parse(draft);
    const a = await repo.save(owner, dto);
    await expect(repo.send(owner, a.id)).rejects.toThrow();
    const b = await repo.save(owner, { ...dto, revision: 1, isDraft: false }, a.id);
    expect(b.revision).toBe(2);
    expect(new Date(b.createdAt).getTime()).toBe(new Date(a.createdAt).getTime());
    expect(new Date((await repo.detail(owner,a.id)).createdAt).getTime()).toBe(new Date(a.createdAt).getTime());
    await expect(repo.save(owner, { ...dto, revision: 1 }, a.id)).rejects.toThrow('đã thay đổi');
    await repo.send(owner, a.id);
    expect((await repo.detail(owner, a.id)).status).toBe('SENT_TO_PORT');
    await repo.recall(owner, a.id);
    expect((await repo.detail(owner, a.id)).status).toBe('VALIDATED');
  });
  it('enforces authentication, active account role, validation and ownership over HTTP', async () => {
    expect((await fetch(base)).status).toBe(401);
    const headers = { 'Content-Type': 'application/json', Authorization: `Bearer ${jwt.sign({ sub: owner }, process.env.JWT_SECRET!)}` };
    const bad = input(); bad.bills[0].containers[0].commodity = '';
    expect((await fetch(base, { method: 'POST', headers, body: JSON.stringify(bad) })).status).toBe(422);
    const valid = input(); valid.bills[0].blNumber = 'HTTP-BL';
    const created = await fetch(base, { method: 'POST', headers, body: JSON.stringify(valid) });
    expect(created.status).toBe(201);
    const doc = (await created.json() as { data: { id: string } }).data;
    const otherHeaders = { Authorization: `Bearer ${jwt.sign({ sub: other }, process.env.JWT_SECRET!)}` };
    expect((await fetch(`${base}/${doc.id}`, { headers: otherHeaders })).status).toBe(404);
    await query("UPDATE users SET is_active=false WHERE id=$1", [other]);
    expect((await fetch(base, { headers: otherHeaders })).status).toBe(403);
  });
  it('persists multiple bills and mixed container cargo without writing master operational state', async () => {
    const dto = input(); dto.bills[0].blNumber = 'MULTI-COLD';
    dto.bills.push({ blNumber: 'MULTI-DRY', shipper: 'Second exporter', consignee: 'Second importer', containers: [
      { containerNumber: 'MSCU6639870', isoType: '20GP', sealNumber: 'DRY-SEAL', grossWeightKg: 11000, commodity: 'Máy móc đóng kiện', isReefer: false },
    ] });
    const before = (await query('SELECT count(*) FROM containers')).rows[0].count;
    const doc = await repo.save(owner, manifestSchema.parse(dto));
    expect(doc.totalContainers).toBe(2);
    expect(doc.totalWeightKg).toBe(34000);
    expect(doc.blGroups.map(b => b.containers[0].commodity)).toEqual(['Cá đông lạnh', 'Máy móc đóng kiện']);
    expect((await query('SELECT count(*) FROM containers')).rows[0].count).toBe(before);
    const changed = structuredClone(dto); changed.bills[0].consignee = 'Changed';
    await expect(repo.save(owner, manifestSchema.parse(changed))).rejects.toThrow('nội dung khác');
  });
  it('derives ISO and reefer fields from existing equipment despite forged request fields', async () => {
    const dto = input(); dto.bills[0].blNumber = 'REGISTERED-ONLY';
    Object.assign(dto.bills[0].containers[0], { isoType: '20GP', isReefer: false });
    const doc = await repo.save(owner, manifestSchema.parse(dto));
    expect(doc.records[0]).toMatchObject({ containerId: coldContainer, isoType: '40RF', isoSizeType: '45R1', isReefer: true });
    expect(await repo.lookupContainer('CSQU3054383')).toMatchObject({ containerId: coldContainer, isoSizeType: '45R1' });
  });
  it('rejects a missing container atomically and allows the same draft after registration', async () => {
    const number = Array.from({ length: 10 }, (_, i) => `CSQU305439${i}`).find(isValidIso6346)!;
    const dto = input(); dto.isDraft = true; dto.bills[0].blNumber = 'REGISTER-LATER'; dto.bills[0].containers[0].containerNumber = number;
    expect(await repo.lookupContainer(number)).toBeNull();
    await expect(repo.save(owner, manifestSchema.parse(dto))).rejects.toThrow('không tồn tại');
    expect((await query('SELECT 1 FROM shipping_manifests WHERE request_key=$1', [dto.requestKey])).rowCount).toBe(0);
    const id = randomUUID();
    try {
      await query(`INSERT INTO containers(id,container_type_id,container_no,iso_size_type) SELECT $1,id,$2,'22G1' FROM container_types WHERE code='20GP'`, [id,number]);
      const doc = await repo.save(owner, manifestSchema.parse(dto));
      expect(doc.records[0]).toMatchObject({ containerId: id, isoType: '20GP', isoSizeType: '22G1', isReefer: false });
    } finally { await query('DELETE FROM containers WHERE id=$1',[id]); }
  });
  it('hides quarantined identities from lookup and prevents adding them even with a valid number', async () => {
    await query('UPDATE containers SET identity_quarantined=true WHERE id=$1',[coldContainer]);
    try {
      expect(await repo.lookupContainer('CSQU3054383')).toBeNull();
      const dto=input();dto.bills[0].blNumber='QUARANTINE-TEST';
      await expect(repo.save(owner,manifestSchema.parse(dto))).rejects.toThrow('không tồn tại');
    } finally { await query('UPDATE containers SET identity_quarantined=false WHERE id=$1',[coldContainer]); }
  });
  it('rejects a bad checksum at the DB boundary and in lookup before a user can add it', async () => {
    expect((await query("SELECT is_valid_container_iso6346('MSKU8829104') AS bad,is_valid_container_iso6346('MSKU8829101') AS good")).rows[0]).toEqual({bad:false,good:true});
    await expect(query('UPDATE containers SET container_no=$1 WHERE id=$2',['CSQU3054384',coldContainer])).rejects.toMatchObject({code:'23514'});
    const headers={Authorization:`Bearer ${jwt.sign({sub:owner},process.env.JWT_SECRET!)}`};
    expect((await fetch(`${base}/container-lookup?containerNumber=MSKU8829104`,{headers})).status).toBe(422);
  });
});

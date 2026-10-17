import { randomUUID } from 'crypto';
import pool, { query } from '../../../infrastructure/database/db';
import { ContainerIntakeService } from '../application/container-intake.service';
import { ContainerRepository } from './container.repository';
import { ContainerIntakeRepository } from './container-intake.repository';
const enabled = process.env.NXP038_INTEGRATION === '1' && process.env.DATABASE_URL === 'postgresql://postgres@127.0.0.1:55438/nxp038_test';
(enabled ? describe : describe.skip)('Carrier declarations against isolated PostgreSQL', () => {
    const userId = randomUUID(), otherUserId = randomUUID();
    const intake = new ContainerIntakeService();
    const repo = new ContainerIntakeRepository();
    let containerId: string;
    const row = { containerNumber: 'EMCU8361795', containerTypeCode: '40RF', cargoType: 'reefer', requiredPowerKw: 12.5, grossWeightKg: 10000 };
    beforeAll(async () => {
        await query("INSERT INTO users(id,username,email,password,role,is_active,created_at,updated_at) VALUES($1,$2,$3,'test-only','Transport Company',true,now(),now()),($4,$5,$6,'test-only','Transport Company',true,now(),now())", [userId, 'test-' + userId, userId + '@example.invalid', otherUserId, 'test-' + otherUserId, otherUserId + '@example.invalid']);
    });
    afterAll(async () => {
        await query('DELETE FROM container_visits WHERE declared_by=ANY($1::uuid[])', [[userId, otherUserId]]);
        if (containerId)
            await query('DELETE FROM containers WHERE id=$1', [containerId]);
        await query('DELETE FROM users WHERE id=ANY($1::uuid[])', [[userId, otherUserId]]);
        await pool.end();
    });
    it('persists matching declaration/master fields with no booking fields', async () => {
        const result = await intake.createManual(row, 'transport_company', userId);
        containerId = result.containerId!;
        expect(result.status).toBe('created_master');
        const saved = (await query('SELECT movement_type,source_reference,seal_number,bl_booking_no,requested_service_date,data_status,required_power_kw FROM container_visits WHERE id=$1', [result.visitId])).rows[0];
        expect(saved).toEqual({ movement_type: null, source_reference: null, seal_number: null, bl_booking_no: null, requested_service_date: null, data_status: null, required_power_kw: '12.50' });
        const detail = await new ContainerRepository().findById(containerId);
        expect(detail).toMatchObject({ cargoType: 'reefer', cargoTypeName: 'Hàng đông lạnh', grossWeightKg: 10000, requiredPowerKw: 12.5 });
        expect(await repo.listTransportDeclarations(userId)).toEqual([expect.objectContaining({ containerId, requiredPowerKw: 12.5, cargoTypeName: 'Hàng đông lạnh' })]);
    });
    it('deduplicates repeated declarations and synchronizes edits', async () => {
        expect((await intake.createManual(row, 'transport_company', userId)).status).toBe('duplicate');
        expect((await intake.createManual({ ...row, requiredPowerKw: 15 }, 'transport_company', userId)).status).toBe('updated_visit');
        expect((await query('SELECT required_power_kw FROM containers WHERE id=$1', [containerId])).rows[0].required_power_kw).toBe('15.00');
        expect((await query('SELECT count(*)::int AS n FROM container_visits WHERE container_id=$1', [containerId])).rows[0].n).toBe(1);
    });
    it('isolates users and rolls back incompatible equipment', async () => {
        expect(await repo.listTransportDeclarations(otherUserId)).toEqual([]);
        await expect(intake.createManual(row, 'transport_company', otherUserId)).rejects.toThrow('tài khoản khác');
        await expect(intake.createManual({ ...row, containerTypeCode: '40HC' }, 'transport_company', userId)).rejects.toThrow('Container lạnh');
        expect((await query('SELECT required_power_kw FROM containers WHERE id=$1', [containerId])).rows[0].required_power_kw).toBe('15.00');
    });
    it('records the declaring user without attributing transport or ownership',async()=>{
        const visit=(await query('SELECT declared_by,transport_company_name FROM container_visits WHERE container_id=$1',[containerId])).rows[0];
        expect(visit).toEqual({declared_by:userId,transport_company_name:null});
        expect((await query('SELECT carrier_id FROM containers WHERE id=$1',[containerId])).rows[0].carrier_id).toBeNull();
    });
    it('persists a provided seal on the visit and current projection, and accepts clearing it', async () => {
        await intake.createManual({ ...row, sealNumber: 'SEAL-123' }, 'transport_company', userId);
        expect((await repo.listTransportDeclarations(userId))[0].sealNumber).toBe('SEAL-123');
        expect((await query('SELECT seal_no FROM containers WHERE id=$1', [containerId])).rows[0].seal_no).toBe('SEAL-123');
        await intake.createManual({ ...row, sealNumber: '' }, 'transport_company', userId);
        expect((await repo.listTransportDeclarations(userId))[0].sealNumber).toBeNull();
        expect((await query('SELECT seal_no FROM containers WHERE id=$1', [containerId])).rows[0].seal_no).toBeNull();
    });
    it('creates a new storage occurrence when a physical Container returns', async () => {
        await query("UPDATE container_visits SET visit_status='completed', completed_at=now() WHERE container_id=$1", [containerId]);
        await query("UPDATE containers SET status='gate_out', left_at=now() WHERE id=$1", [containerId]);
        const result = await intake.createManual(row, 'transport_company', userId);
        expect(result.status).toBe('created_visit');
        expect((await query('SELECT status,left_at FROM containers WHERE id=$1', [containerId])).rows[0]).toEqual({status:'expected',left_at:null});
        expect((await query('SELECT count(*)::int AS n FROM container_visits WHERE container_id=$1', [containerId])).rows[0].n).toBe(2);
    });
});

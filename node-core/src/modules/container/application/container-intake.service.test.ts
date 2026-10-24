import * as XLSX from 'xlsx';
import { ContainerIntakeService } from './container-intake.service';
import { ContainerIntakeRepository } from '../infrastructure/container-intake.repository';
import { containerIntakeSchema } from './container-intake.validator';
const userId = '5ce5cd9b-1da8-441e-8442-04b9cb7f76c7';
const declaration = { containerNumber: 'EMCU8361795', containerTypeCode: '40HC', loadStatus: 'full', cargoType: 'general' };
const file = (rows: Record<string, unknown>[]) => { const wb = XLSX.utils.book_new(); XLSX.utils.book_append_sheet(wb, XLSX.utils.json_to_sheet(rows), 'CongTyVanChuyen'); const buffer = XLSX.write(wb, { type: 'buffer', bookType: 'xlsx' }); return { originalname: 'containers.xlsx', size: buffer.length, buffer } as Express.Multer.File; };
const makeService = () => {
    const repository = { processRow: jest.fn().mockResolvedValue({ status: 'created_master', errors: [] }), listCargoTypes: jest.fn().mockResolvedValue([{ code: 'general', name: 'Hàng thông thường', requiresPower: false }, { code: 'reefer', name: 'Hàng đông lạnh', requiresPower: true }]), listContainerTypes: jest.fn().mockResolvedValue([{ code: '40RF', description: 'Container lạnh 40 feet' }]), createBatch: jest.fn().mockResolvedValue('batch'), saveRowResult: jest.fn(), completeBatch: jest.fn() } as unknown as jest.Mocked<ContainerIntakeRepository>;
    return { repository, service: new ContainerIntakeService(repository) };
};
describe('Carrier yard declarations', () => {
    it('accepts declaration without booking direction, date, seal, B/L or reference', async () => {
        const { service, repository } = makeService();
        await service.createManual(declaration, 'transport_company', userId);
        expect(repository.processRow).toHaveBeenCalledWith(expect.objectContaining({ sourceType: 'transport_company', containerNumber: 'EMCU8361795' }), userId);
        expect(repository.processRow.mock.calls[0][0]).not.toHaveProperty('movementType');
    });
    it.each(['transportCompanyName', 'movementType', 'requestedServiceDate', 'requestedPickupDate', 'sourceReference', 'blBookingNumber'])('rejects removed field %s', async (key) => {
        const { service, repository } = makeService();
        await expect(service.createManual({ ...declaration, [key]: 'legacy' }, 'transport_company', userId)).rejects.toThrow();
        expect(repository.processRow).not.toHaveBeenCalled();
    });
    it.each([undefined, null, '', '   ', ' SEAL-123 '])('accepts optional seal (%s) without inventing a number', async sealNumber => {
        const { service, repository } = makeService();
        await service.createManual({ ...declaration, sealNumber }, 'transport_company', userId);
        expect(repository.processRow.mock.calls[0][0].sealNumber).toBe(sealNumber === undefined ? undefined : sealNumber?.trim() || null);
    });
    it('imports declarations without a transport-company column', async () => {
        const {service,repository}=makeService();
        const result=await service.importExcel(file([{container_number:'EMCU8361795',container_type:'40HC'}]),'transport_company',userId);
        expect(result).toMatchObject({successRows:1,failedRows:0});
        expect(repository.processRow.mock.calls[0][0]).not.toHaveProperty('transportCompanyName');
    });
    it.each(['Tên công ty vận tải','transport_company_name'])('rejects stale templates with company column %s',async header=>{
        const {service,repository}=makeService();
        await expect(service.importExcel(file([{container_number:'EMCU8361795',container_type:'40HC',[header]:'Công ty A'}]),'transport_company',userId)).rejects.toThrow('File mẫu cũ');
        expect(repository.processRow).not.toHaveBeenCalled();
    });
    it('refuses the removed port source', async () => { const { service, repository } = makeService(); await expect(service.createManual(declaration, 'port_vessel', userId)).rejects.toThrow('Chỉ công ty'); expect(repository.processRow).not.toHaveBeenCalled(); });
    it.each([null, 0, -1, 'abc', Infinity])('requires positive finite kW for frozen cargo (%s)', value => { expect(containerIntakeSchema.safeParse({ ...declaration, sourceType: 'transport_company', cargoType: 'reefer', requiredPowerKw: value }).success).toBe(false); });
    it('requires power only for frozen cargo', () => {
        expect(containerIntakeSchema.safeParse({ ...declaration, sourceType: 'transport_company', cargoType: 'reefer', requiredPowerKw: 12.5 }).success).toBe(true);
        expect(containerIntakeSchema.safeParse({ ...declaration, sourceType: 'transport_company', requiredPowerKw: 12.5 }).success).toBe(false);
    });
    it('imports Vietnamese cargo names from the DB catalog', async () => {
        const { service, repository } = makeService();
        const result = await service.importExcel(file([{ 'Số Container': 'EMCU8361795', 'Loại Container': '40RF', 'Loại hàng': 'Hàng đông lạnh', 'Công suất điện (kW)': 12.5 }]), 'transport_company', userId);
        expect(result.failedRows).toBe(0);
        expect(repository.processRow).toHaveBeenCalledWith(expect.objectContaining({ cargoType: 'reefer', requiredPowerKw: 12.5 }), userId, 'batch', 2);
    });
    it('rejects stale Excel templates instead of silently dropping booking fields', async () => {
        const { service, repository } = makeService();
        await expect(service.importExcel(file([{ ...declaration, movement_type: 'truck_dropoff' }]), 'transport_company', userId)).rejects.toThrow('File mẫu cũ');
        expect(repository.createBatch).not.toHaveBeenCalled();
    });
    it('exports one empty Container sheet with every manual declaration field', async () => {
        const { service } = makeService();
        const wb = XLSX.read(await service.buildTemplate(), { type: 'buffer' });
        expect(wb.SheetNames).toEqual(['Container']);
        expect(XLSX.utils.sheet_to_json<string[]>(wb.Sheets.Container, { header: 1 })).toEqual([[
            'Số Container', 'Loại Container', 'Tên khách hàng/chủ hàng',
            'Tình trạng hàng', 'Loại hàng', 'Khối lượng (kg)', 'Công suất điện yêu cầu (kW)',
            'Số seal (nếu có)',
        ]]);
        expect(XLSX.utils.sheet_to_json(wb.Sheets.Container)).toEqual([]);
    });
    it('imports the downloaded template with exactly the same data as manual entry', async () => {
        const { service, repository } = makeService();
        const wb = XLSX.read(await service.buildTemplate(), { type: 'buffer' });
        XLSX.utils.sheet_add_aoa(wb.Sheets.Container, [[
            'EMCU8361795', '40RF', 'Chủ hàng',
            'Đầy hàng', 'Hàng đông lạnh', 10000, 12.5, 'SEAL-123',
        ]], { origin: 'A2' });
        const buffer = XLSX.write(wb, { type: 'buffer', bookType: 'xlsx' });
        const result = await service.importExcel({ originalname: 'container.xlsx', size: buffer.length, buffer } as Express.Multer.File, 'transport_company', userId);
        expect(result.failedRows).toBe(0);
        expect(repository.processRow).toHaveBeenCalledWith({
            containerNumber: 'EMCU8361795', containerTypeCode: '40RF', customerName: 'Chủ hàng',
 loadStatus: 'full', cargoType: 'reefer',
            grossWeightKg: 10000, requiredPowerKw: 12.5, sourceType: 'transport_company',
            sealNumber: 'SEAL-123',
        }, userId, 'batch', 2);
    });
});

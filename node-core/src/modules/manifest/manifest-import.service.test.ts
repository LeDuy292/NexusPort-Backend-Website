import { randomUUID } from 'crypto';
import * as XLSX from 'xlsx';
import { ManifestImportService, MANIFEST_IMPORT_HEADERS } from './manifest-import.service';

const cold = 'CMAU3381923', dry = 'MSKU8829101', second = 'DEMU6111017';
const equipment = [cold, dry, second].map((containerNumber, i) => ({ containerId: randomUUID(), containerNumber, isoType: i === 0 ? '20RF' : '20GP', isoSizeType: i === 0 ? '22R1' : '22G1', isReefer: i === 0 }));
const row = (number = cold, bl = 'BL-EXCEL-001'): unknown[] => ['EG-2605W', 'EVER GLORY', 'EXPORT', bl, 'Người gửi', 'Người nhận', number, 'SEAL-001', 18000, 'Hàng đóng thùng carton'];
const file = (rows: unknown[][], headers: unknown[] = [...MANIFEST_IMPORT_HEADERS], edit?: (sheet: XLSX.WorkSheet, wb: XLSX.WorkBook) => void) => {
  const wb = XLSX.utils.book_new(), sheet = XLSX.utils.aoa_to_sheet([headers, ...rows]);
  XLSX.utils.book_append_sheet(wb, sheet, 'Manifest'); edit?.(sheet, wb);
  const buffer = XLSX.write(wb, { type: 'buffer', bookType: 'xlsx', compression: true }) as Buffer;
  return { originalname: 'Manifest.xlsx', buffer, size: buffer.length } as Express.Multer.File;
};
const repository = { lookupContainers: jest.fn(), usedBillNumbers: jest.fn() };
const service = new ManifestImportService(repository);
beforeEach(() => { repository.lookupContainers.mockResolvedValue(equipment); repository.usedBillNumbers.mockResolvedValue([]); });

describe('Single-sheet Manifest Excel import preview', () => {
  it('groups non-contiguous rows into bills, derives registered ISO/reefer identity and leaves persistence to the existing save flow', async () => {
    const result = await service.preview(file([row(cold), row(dry, 'BL-EXCEL-002'), row(second)]), 'owner');
    expect(result.valid).toBe(true);
    if (!result.valid) throw new Error('Expected valid preview');
    expect(result).toMatchObject({ totalRows: 3, totalBills: 2, totalWeightKg: 54000 });
    expect(result.manifest.bills.map(b => b.containers.length)).toEqual([2, 1]);
    expect(result.manifest.bills[0].containers[0]).toMatchObject({ ...equipment[0], commodity: 'Hàng đóng thùng carton', grossWeightKg: 18000 });
    expect(result.manifest.bills[1].containers[0]).toMatchObject({ ...equipment[1], isReefer: false });
    expect(repository.usedBillNumbers).toHaveBeenCalledWith('owner', ['BL-EXCEL-001', 'BL-EXCEL-002']);
    expect(result.manifest).not.toHaveProperty('id');
    expect(result.manifest.requestKey).toMatch(/^[a-f0-9-]{36}$/);
  });
  it('accepts reordered columns, Vietnamese directions, unaccented headers and normalized container numbers', async () => {
    const values = row(' cmau-3381923 '); values[2] = 'Xuất khẩu';
    const result = await service.preview(file([values.reverse()], [...MANIFEST_IMPORT_HEADERS].reverse().map(h => h.normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/g, 'd'))), 'owner');
    expect(result.valid).toBe(true);
    if (result.valid) expect(result.manifest.bills[0].containers[0].containerNumber).toBe(cold);
  });
  it('reports actual Excel row and column for bad checksum, required fields and text weights without accessing DB', async () => {
    const values = row('CMAU3381920'); values[4] = ''; values[8] = '18,000'; values[9] = '';
    const result = await service.preview(file([[], values]), 'owner');
    expect(result.valid).toBe(false);
    expect(result.errors).toEqual(expect.arrayContaining([
      expect.objectContaining({ row: 3, column: 'Số container', message: expect.stringContaining('ISO 6346') }),
      expect.objectContaining({ row: 3, column: 'Người gửi hàng' }),
      expect.objectContaining({ row: 3, column: 'Trọng lượng (kg)' }),
      expect.objectContaining({ row: 3, column: 'Mô tả hàng hóa' }),
    ]));
    expect(repository.lookupContainers).not.toHaveBeenCalled();
  });
  it.each([0, -1, 36001, Infinity])('rejects invalid gross weight %s', async weight => {
    const values = row(); values[8] = weight;
    expect((await service.preview(file([values]), 'owner')).valid).toBe(false);
  });
  it('rejects mixed manifests and contradictory bill metadata', async () => {
    const values = row(dry); values[0] = 'OTHER'; values[1] = 'OTHER VESSEL'; values[2] = 'IMPORT'; values[5] = 'Other consignee';
    const result = await service.preview(file([row(), values]), 'owner');
    expect(result.valid).toBe(false);
    expect(result.totalErrors).toBe(4);
    expect(result.errors.every(e => e.row === 3)).toBe(true);
  });
  it('rejects a repeated container across bills', async () => {
    const result = await service.preview(file([row(), row(cold, 'BL-OTHER')]), 'owner');
    expect(result.errors).toContainEqual({ row: 3, column: 'Số container', message: 'Container đã xuất hiện ở dòng 2.' });
  });
  it('reports missing/quarantined identities and bills already used by the current owner', async () => {
    repository.lookupContainers.mockResolvedValue([]);
    repository.usedBillNumbers.mockResolvedValue(['BL-EXCEL-001']);
    const result = await service.preview(file([row()]), 'owner');
    expect(result.valid).toBe(false);
    expect(result.totalErrors).toBe(2);
    expect(result).not.toHaveProperty('manifest');
    expect(result.errors.map(e => e.column)).toEqual(['Số container', 'Số vận đơn (B/L)']);
  });
  it('requires one sheet and refuses merged cells, formula cells and Excel errors', async () => {
    await expect(service.preview(file([row()], undefined, (_s, wb) => XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet([['extra']]), 'Extra')), 'owner')).rejects.toThrow('đúng 1 sheet');
    await expect(service.preview(file([row()], undefined, s => { s['!merges'] = [XLSX.utils.decode_range('A2:A3')]; }), 'owner')).rejects.toThrow('Không gộp ô');
    const formula = await service.preview(file([row()], undefined, s => { s.I2 = { t:'n', v:18000, f:'900*20' }; }), 'owner');
    expect(formula.errors[0]).toMatchObject({ row:2, column:'Trọng lượng (kg)' });
    const error = await service.preview(file([row()], undefined, s => { s.I2 = { t:'e', v:7 }; }), 'owner');
    expect(error.valid).toBe(false);
  });
  it('refuses missing/duplicate/unsupported headers and extra unlabelled data', async () => {
    await expect(service.preview(file([row()], [...MANIFEST_IMPORT_HEADERS].slice(1)), 'owner')).rejects.toThrow('Thiếu hoặc trùng');
    await expect(service.preview(file([row()], [...MANIFEST_IMPORT_HEADERS, MANIFEST_IMPORT_HEADERS[0]]), 'owner')).rejects.toThrow('Thiếu hoặc trùng');
    await expect(service.preview(file([[...row(), '40HC']], [...MANIFEST_IMPORT_HEADERS, 'Loại ISO']), 'owner')).rejects.toThrow('Cột không có');
    const result = await service.preview(file([[...row(), 'unexpected']], [...MANIFEST_IMPORT_HEADERS, '']), 'owner');
    expect(result.valid).toBe(false);
    expect(result.errors[0]).toMatchObject({ row:2, column:'Cột 11' });
  });
  it('rejects an empty file, invalid archive, oversized input and misleading extensions', async () => {
    await expect(service.preview(file([]), 'owner')).rejects.toThrow('chưa có container');
    await expect(service.preview(undefined, 'owner')).rejects.toThrow('.xlsx');
    await expect(service.preview({ ...file([row()]), originalname:'data.csv' }, 'owner')).rejects.toThrow('.xlsx');
    await expect(service.preview({ ...file([row()]), buffer:Buffer.alloc(100) }, 'owner')).rejects.toThrow('không hợp lệ');
    await expect(service.preview({ ...file([row()]), buffer:Buffer.alloc(5*1024*1024+1) }, 'owner')).rejects.toThrow('5 MB');
  });
  it('rejects archives claiming excessive expanded content before parsing Excel', async () => {
    const input = file([row()]);
    const central = input.buffer.indexOf(Buffer.from([0x50,0x4b,0x01,0x02]));
    input.buffer.writeUInt32LE(100*1024*1024, central+24);
    await expect(service.preview(input, 'owner')).rejects.toThrow('giải nén');
  });
  it('bounds row and per-bill limits and caps error output without returning partial manifests', async () => {
    const result = await service.preview(file(Array.from({length:501}, () => row())), 'owner');
    expect(result.valid).toBe(false);
    expect(result.totalErrors).toBe(501);
    expect(result.errors).toHaveLength(200);
    await expect(service.preview(file(Array.from({length:5001}, () => row())), 'owner')).rejects.toThrow('5.000 container');
    expect(repository.lookupContainers).not.toHaveBeenCalled();
  });
  it('produces a blank one-sheet template with exactly the demonstrated headers', () => {
    const wb = XLSX.read(service.buildTemplate(), {type:'buffer'});
    expect(wb.SheetNames).toEqual(['Manifest']);
    expect(XLSX.utils.sheet_to_json(wb.Sheets.Manifest, {header:1})).toEqual([[...MANIFEST_IMPORT_HEADERS]]);
  });
});

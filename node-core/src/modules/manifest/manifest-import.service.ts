import { randomUUID } from 'crypto';
import * as XLSX from 'xlsx';
import { z } from 'zod';
import { ValidationError } from '../../shared/errors/app-error';
import { isValidIso6346, normalizeContainerNumber } from '../container/application/container.validator';
import { ManifestRepository } from './manifest.repository';
import { ManifestInput, manifestSchema } from './manifest.schema';

export const MANIFEST_IMPORT_HEADERS = [
  'Chuyến tàu', 'Tên tàu', 'Hướng khai', 'Số vận đơn (B/L)', 'Người gửi hàng',
  'Người nhận hàng', 'Số container', 'Số niêm chì', 'Trọng lượng (kg)', 'Mô tả hàng hóa',
] as const;
const keys = ['voyageNumber', 'vesselName', 'direction', 'blNumber', 'shipper', 'consignee', 'containerNumber', 'sealNumber', 'grossWeightKg', 'commodity'] as const;
const normalize = (s: string) => s.trim().normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/gi, 'd').toLowerCase().replace(/[^a-z0-9]/g, '');
const text = (max: number) => z.string({ invalid_type_error: 'Nhập dữ liệu dạng chữ.' }).trim().min(1, 'Không được để trống.').max(max, `Tối đa ${max} ký tự.`);
const rowSchema = z.object({
  voyageNumber: text(100), vesselName: text(250),
  direction: z.preprocess(v => typeof v === 'string' ? ({ xuatkhau: 'EXPORT', nhapkhau: 'IMPORT' }[normalize(v)] || v.trim().toUpperCase()) : v,
    z.enum(['IMPORT', 'EXPORT'], { errorMap: () => ({ message: 'Chọn IMPORT (nhập khẩu) hoặc EXPORT (xuất khẩu).' }) })),
  blNumber: text(100).transform(v => v.toUpperCase()), shipper: text(500), consignee: text(500),
  containerNumber: text(30).transform(normalizeContainerNumber).refine(isValidIso6346, 'Số container sai chuẩn ISO 6346.'),
  sealNumber: text(50),
  // Text with grouping separators is ambiguous across Excel locales. Require a numeric cell.
  grossWeightKg: z.number({ invalid_type_error: 'Nhập ô dạng số, không thêm kg hoặc dấu phân cách bằng tay.' }).finite().positive('Trọng lượng phải lớn hơn 0.').max(36000, 'Trọng lượng không được vượt quá 36.000 kg.'),
  commodity: text(2000),
});
export type ManifestImportError = { row: number; column: string; message: string };
type ImportRow = z.infer<typeof rowSchema> & { row: number };
export const MAX_IMPORT_ROWS = 5000;
const MAX_ERRORS = 200;

// Bound expanded ZIP content before the Excel reader allocates it. ZIP64 is not
// needed for a 5 MB upload and is rejected rather than bypassing these limits.
function checkArchive(buffer: Buffer) {
  const invalid = () => new ValidationError('File .xlsx không hợp lệ hoặc quá lớn sau khi giải nén.');
  if (buffer.readUInt32LE(0) !== 0x04034b50) throw invalid();
  let eocd = -1;
  for (let i = buffer.length - 22; i >= Math.max(0, buffer.length - 65557); i--) {
    if (buffer.readUInt32LE(i) === 0x06054b50 && i + 22 + buffer.readUInt16LE(i + 20) === buffer.length) { eocd = i; break; }
  }
  if (eocd < 0 || buffer.readUInt16LE(eocd + 4) !== 0 || buffer.readUInt16LE(eocd + 6) !== 0) throw invalid();
  const count = buffer.readUInt16LE(eocd + 10);
  if (count > 2000 || count === 0 || buffer.readUInt16LE(eocd + 8) !== count) throw invalid();
  let offset = buffer.readUInt32LE(eocd + 16), expanded = 0;
  for (let i = 0; i < count; i++) {
    if (offset + 46 > eocd || buffer.readUInt32LE(offset) !== 0x02014b50) throw invalid();
    expanded += buffer.readUInt32LE(offset + 24);
    if (expanded > 30 * 1024 * 1024 || buffer.readUInt16LE(offset + 8) & 1) throw invalid();
    offset += 46 + buffer.readUInt16LE(offset + 28) + buffer.readUInt16LE(offset + 30) + buffer.readUInt16LE(offset + 32);
  }
  if (offset !== eocd) throw invalid();
}

export class ManifestImportService {
  constructor(private readonly repository: Pick<ManifestRepository, 'lookupContainers' | 'usedBillNumbers'> = new ManifestRepository()) {}

  buildTemplate(): Buffer {
    const wb = XLSX.utils.book_new();
    const sheet = XLSX.utils.aoa_to_sheet([[...MANIFEST_IMPORT_HEADERS]]);
    sheet['!cols'] = [16, 22, 16, 24, 38, 38, 22, 22, 22, 70].map(wch => ({ wch }));
    sheet['!autofilter'] = { ref: 'A1:J1' };
    XLSX.utils.book_append_sheet(wb, sheet, 'Manifest');
    return XLSX.write(wb, { type: 'buffer', bookType: 'xlsx' });
  }

  async preview(file: Express.Multer.File | undefined, owner: string) {
    if (!file || !/\.xlsx$/i.test(file.originalname)) throw new ValidationError('Vui lòng chọn file Excel .xlsx theo mẫu Manifest.');
    if (file.buffer.length < 22 || file.buffer.length > 5 * 1024 * 1024) throw new ValidationError('File Excel không hợp lệ hoặc vượt quá 5 MB.');
    checkArchive(file.buffer);
    let wb: XLSX.WorkBook;
    try { wb = XLSX.read(file.buffer, { type: 'buffer', sheetRows: 10002, cellFormula: true, cellHTML: false, cellText: false }); }
    catch { throw new ValidationError('Không thể đọc file Excel. Vui lòng lưu lại dưới dạng .xlsx và thử lại.'); }
    if (wb.SheetNames.length !== 1) throw new ValidationError('File Manifest phải có đúng 1 sheet. Mỗi file dùng cho 1 bản khai.');
    const sheet = wb.Sheets[wb.SheetNames[0]];
    if (!sheet?.['!ref']) throw new ValidationError('File Excel chưa có dữ liệu.');
    const extent = XLSX.utils.decode_range(sheet['!fullref'] || sheet['!ref']);
    if (extent.e.r > 10000 || extent.e.c > 49) throw new ValidationError('File vượt phạm vi cho phép. Giữ tối đa 5.000 dòng container và các cột trong mẫu.');
    if (sheet['!merges']?.length) throw new ValidationError('Không gộp ô trong file import. Hãy lặp đầy đủ thông tin trên mỗi dòng.');
    for (let col = 0; col <= extent.e.c; col++) {
      const cell = sheet[XLSX.utils.encode_cell({ r: 0, c: col })];
      if (cell?.f || cell?.t === 'e') throw new ValidationError('Tiêu đề cột phải là chữ theo mẫu, không dùng công thức hoặc ô lỗi Excel.');
    }
    const headers = Array.from({ length: extent.e.c + 1 }, (_, col) => String(sheet[XLSX.utils.encode_cell({ r: 0, c: col })]?.v ?? '').trim());
    const positions = keys.map((key, i) => headers.map((h, col) => normalize(h) === normalize(MANIFEST_IMPORT_HEADERS[i]) || normalize(h) === normalize(key) ? col : -1).filter(col => col >= 0));
    const missing = positions.flatMap((p, i) => p.length !== 1 ? [MANIFEST_IMPORT_HEADERS[i]] : []);
    if (missing.length) throw new ValidationError(`Thiếu hoặc trùng tiêu đề cột: ${missing.join(', ')}. Hàng đầu tiên phải là tiêu đề theo mẫu.`);
    const selected = new Set(positions.map(p => p[0]));
    const unknown = headers.filter((h, col) => h && !selected.has(col));
    if (unknown.length) throw new ValidationError(`Cột không có trong mẫu: ${unknown.join(', ')}. ISO và cont lạnh được tự lấy từ hệ thống.`);
    const errors: ManifestImportError[] = [];
    let totalErrors = 0, totalRows = 0;
    const add = (row: number, column: string, message: string) => { totalErrors++; if (errors.length < MAX_ERRORS) errors.push({ row, column, message }); };
    const rows: ImportRow[] = [];
    for (let r = 1; r <= extent.e.r; r++) {
      const cells = headers.map((_, c) => sheet[XLSX.utils.encode_cell({ r, c })] as XLSX.CellObject | undefined);
      if (!cells.some(cell => cell && (cell.f || cell.v !== undefined && String(cell.v).trim() !== ''))) continue;
      if (++totalRows > MAX_IMPORT_ROWS) throw new ValidationError('Mỗi file chỉ được chứa tối đa 5.000 container.');
      let unsupported = false;
      cells.forEach((cell, c) => {
        if (cell?.f || cell?.t === 'e') { add(r + 1, headers[c] || `Cột ${c + 1}`, 'Nhập giá trị trực tiếp, không dùng công thức hoặc ô lỗi Excel.'); unsupported = true; }
        if (!selected.has(c) && cell?.v !== undefined && String(cell.v).trim()) { add(r + 1, `Cột ${c + 1}`, 'Có dữ liệu ở cột không có tiêu đề trong mẫu.'); unsupported = true; }
      });
      if (unsupported) continue;
      const raw = Object.fromEntries(keys.map((key, i) => [key, cells[positions[i][0]]?.v ?? '']));
      const parsed = rowSchema.safeParse(raw);
      if (!parsed.success) parsed.error.issues.forEach(issue => add(r + 1, MANIFEST_IMPORT_HEADERS[keys.indexOf(issue.path[0] as typeof keys[number])], issue.message));
      else rows.push({ ...parsed.data, row: r + 1 });
    }
    if (!totalRows) throw new ValidationError('File chưa có container. Nhập dữ liệu từ dòng 2, mỗi dòng là một container.');
    const bills = new Map<string, ImportRow[]>(), containers = new Map<string, number>();
    rows.forEach(row => {
      const first = rows[0];
      for (const key of ['voyageNumber', 'vesselName', 'direction'] as const) {
        if (row[key] !== first[key]) add(row.row, MANIFEST_IMPORT_HEADERS[keys.indexOf(key)], `Phải giống dòng ${first.row}. Một file chỉ chứa một bản khai.`);
      }
      const previous = containers.get(row.containerNumber);
      if (previous) add(row.row, 'Số container', `Container đã xuất hiện ở dòng ${previous}.`);
      else containers.set(row.containerNumber, row.row);
      const group = bills.get(row.blNumber) || [];
      if (group.length) for (const key of ['shipper', 'consignee'] as const) {
        if (row[key] !== group[0][key]) add(row.row, MANIFEST_IMPORT_HEADERS[keys.indexOf(key)], `Thông tin vận đơn ${row.blNumber} khác dòng ${group[0].row}.`);
      }
      group.push(row); bills.set(row.blNumber, group);
      if (group.length === 501) add(row.row, 'Số vận đơn (B/L)', 'Một vận đơn chỉ được có tối đa 500 container.');
    });
    if (bills.size > 100) add(1, 'Số vận đơn (B/L)', 'Một bản khai chỉ được có tối đa 100 vận đơn.');
    const invalid = () => ({ valid: false as const, totalRows, totalBills: bills.size, errors, totalErrors });
    if (totalErrors) return invalid();
    const equipment = await this.repository.lookupContainers([...containers.keys()]);
    const byNumber = new Map(equipment.map(c => [c.containerNumber, c]));
    const usedBills = new Set(await this.repository.usedBillNumbers(owner, [...bills.keys()]));
    rows.forEach(row => {
      if (!byNumber.has(row.containerNumber)) add(row.row, 'Số container', `Container ${row.containerNumber} không tồn tại trong hệ thống. Vui lòng khai báo container trước.`);
      if (usedBills.has(row.blNumber)) add(row.row, 'Số vận đơn (B/L)', `Vận đơn ${row.blNumber} đã được sử dụng trong bản khai khác.`);
    });
    if (totalErrors) return invalid();
    const first = rows[0];
    const manifest: ManifestInput = manifestSchema.parse({
      requestKey: randomUUID(), voyageNumber: first.voyageNumber, vesselName: first.vesselName, direction: first.direction, isDraft: true,
      bills: [...bills].map(([blNumber, group]) => ({ blNumber, shipper: group[0].shipper, consignee: group[0].consignee,
        containers: group.map(row => ({ ...byNumber.get(row.containerNumber)!, sealNumber: row.sealNumber, grossWeightKg: row.grossWeightKg, commodity: row.commodity })),
      })),
    });
    return { valid: true as const, totalRows, totalBills: bills.size, totalWeightKg: rows.reduce((sum, row) => sum + row.grossWeightKg, 0), manifest, errors: [], totalErrors: 0 };
  }
}

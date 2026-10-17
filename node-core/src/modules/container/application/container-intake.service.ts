import * as XLSX from 'xlsx';
import { AppError, ForbiddenError, ValidationError } from '../../../shared/errors/app-error';
import { ContainerIntakeRepository } from '../infrastructure/container-intake.repository';
import { ContainerIntakeResult, ContainerIntakeRow, ContainerIntakeSource } from './container-intake.dto';
import { containerIntakeSchema } from './container-intake.validator';
const normalize = (v: string) => v.trim().toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/g, 'd').replace(/[^a-z0-9]/g, '');
const HEADERS: Record<string, keyof ContainerIntakeRow> = {
    containernumber: 'containerNumber', containerno: 'containerNumber', socontainer: 'containerNumber',
    containertype: 'containerTypeCode', containertypecode: 'containerTypeCode', loaicontainer: 'containerTypeCode',
    seal: 'sealNumber', sealnumber: 'sealNumber', soseal: 'sealNumber', sosealneuco: 'sealNumber',
    loadstatus: 'loadStatus', tinhtranghang: 'loadStatus', cargotype: 'cargoType', loaihang: 'cargoType',
    grossweightkg: 'grossWeightKg', khoiluongkg: 'grossWeightKg', trongluongkg: 'grossWeightKg',
    requiredpowerkw: 'requiredPowerKw', congsuatdienkw: 'requiredPowerKw', congsuatdienyeucaukw: 'requiredPowerKw',
    customername: 'customerName', tenkhachhang: 'customerName', tenkhachhangchuhang: 'customerName',
};
// Old templates must fail visibly; silently discarding booking data is misleading.
const REMOVED = new Set(['transportcompanyname', 'tencongtyvanchuyen', 'tencongtyvantai', 'movementtype', 'nhucau', 'sourcereference', 'mathamchieu', 'blbookingnumber', 'soblbooking', 'requestedservicedate', 'requestedpickupdate', 'ngaymongmuonnhan', 'ngaymongmuonthuchien', 'expectedarrivalat', 'expectedavailableat', 'vesselcallcode']);
export class ContainerIntakeService {
    constructor(private readonly repository = new ContainerIntakeRepository()) { }
    private checkSource(source: ContainerIntakeSource) {
        if (source !== 'transport_company')
            throw new ForbiddenError('Chỉ công ty vận tải được khai báo Container cho kho bãi.');
    }
    getCargoTypes() { return this.repository.listCargoTypes(); }
    async buildTemplate() {
        const workbook = XLSX.utils.book_new();
        const sheet = XLSX.utils.aoa_to_sheet([[
            'Số Container', 'Loại Container', 'Tên khách hàng/chủ hàng',
            'Tình trạng hàng', 'Loại hàng', 'Khối lượng (kg)', 'Công suất điện yêu cầu (kW)',
            'Số seal (nếu có)',
        ]]);
        sheet['!cols'] = [22, 20, 30, 22, 24, 20, 34, 24].map(wch => ({ wch }));
        sheet['!rows'] = [{ hpt: 26 }];
        sheet['!autofilter'] = { ref: 'A1:H1' };
        XLSX.utils.book_append_sheet(workbook, sheet, 'Container');
        return XLSX.write(workbook, { type: 'buffer', bookType: 'xlsx' }) as Buffer;
    }
    async createManual(raw: unknown, source: ContainerIntakeSource, userId: string) {
        this.checkSource(source);
        const parsed = containerIntakeSchema.safeParse({ ...((raw ?? {}) as object), sourceType: source });
        if (!parsed.success) {
            const details = parsed.error.issues.reduce<Record<string, string[]>>((r, i) => { const key = i.path.join('.') || 'request'; r[key] = [...(r[key] ?? []), i.message]; return r; }, {});
            throw new ValidationError('Thông tin khai báo Container không hợp lệ.', details);
        }
        return this.repository.processRow(parsed.data, userId);
    }
    async importExcel(file: Express.Multer.File | undefined, source: ContainerIntakeSource, userId: string) {
        this.checkSource(source);
        if (!file || !/\.(xlsx|xls)$/i.test(file.originalname))
            throw new ValidationError('Vui lòng chọn file Excel .xlsx hoặc .xls.');
        let workbook: XLSX.WorkBook;
        try {
            workbook = XLSX.read(file.buffer, { type: 'buffer' });
        }
        catch {
            throw new ValidationError('Không thể đọc file Excel.');
        }
        const sheet = workbook.Sheets.Container ?? workbook.Sheets.CongTyVanChuyen ?? workbook.Sheets[workbook.SheetNames[0]];
        if (!sheet)
            throw new ValidationError('File Excel không có sheet dữ liệu.');
        const rawRows = XLSX.utils.sheet_to_json<Record<string, unknown>>(sheet, { defval: '', raw: true });
        if (!rawRows.length || rawRows.length > 1000)
            throw new ValidationError('File phải có từ 1 đến 1.000 dòng.');
        const removed = Object.keys(rawRows[0]).filter(h => REMOVED.has(normalize(h)));
        if (removed.length)
            throw new ValidationError('File mẫu cũ chứa trường đã bỏ. Vui lòng tải mẫu khai báo mới.');
        const cargoTypes = await this.repository.listCargoTypes();
        const batchId = await this.repository.createBatch(source, file.originalname, file.size, userId);
        const results: ContainerIntakeResult[] = [];
        for (let index = 0; index < rawRows.length; index++) {
            const raw = rawRows[index];
            const mapped: Record<string, unknown> = { sourceType: source };
            for (const [header, value] of Object.entries(raw)) {
                const key = HEADERS[normalize(header)];
                if (!key)
                    continue;
                mapped[key] = typeof value === 'string' ? value.trim() : value;
                if (key === 'cargoType' && typeof value === 'string')
                    mapped[key] = cargoTypes.find(t => normalize(t.code) === normalize(value) || normalize(t.name) === normalize(value))?.code ?? value;
                if (key === 'loadStatus' && typeof value === 'string')
                    mapped[key] = ({ dayhang: 'full', day: 'full', rong: 'empty', chuaxacdinh: 'unknown' } as Record<string, string>)[normalize(value)] ?? value;
            }
            const parsed = containerIntakeSchema.safeParse(mapped);
            const rowNumber = index + 2;
            let result: ContainerIntakeResult;
            if (!parsed.success)
                result = { rowNumber, containerNumber: String(mapped.containerNumber || ''), status: 'rejected', errors: parsed.error.issues.map(i => `${i.path.join('.')}: ${i.message}`) };
            else {
                try {
                    result = await this.repository.processRow(parsed.data, userId, batchId, rowNumber);
                }
                catch (error) {
                    result = { rowNumber, containerNumber: parsed.data.containerNumber, status: 'rejected', errors: [error instanceof AppError ? error.message : 'Không thể lưu dòng dữ liệu.'] };
                }
            }
            results.push(result);
            await this.repository.saveRowResult(batchId, result, raw);
        }
        await this.repository.completeBatch(batchId, results);
        return { batchId, totalRows: results.length, successRows: results.filter(r => ['created_master', 'created_visit', 'updated_visit'].includes(r.status)).length, duplicateRows: results.filter(r => r.status === 'duplicate').length, failedRows: results.filter(r => r.status === 'rejected').length, rows: results };
    }
    listImports() { return this.repository.listBatches(); }
    listTransportDeclarations(userId: string) { return this.repository.listTransportDeclarations(userId); }
}

import * as XLSX from 'xlsx';
import { AppError, ValidationError } from '../../../shared/errors/app-error';
import { ContainerIntakeRepository } from '../infrastructure/container-intake.repository';
import {
  ContainerIntakeResult, ContainerIntakeRow, ContainerIntakeSource,
} from './container-intake.dto';
import { containerIntakeSchema } from './container-intake.validator';

const HEADERS: Record<string, keyof ContainerIntakeRow> = {
  containerid: 'containerNumber', container_no: 'containerNumber', socontainer: 'containerNumber',
  container_number: 'containerNumber', containernumber: 'containerNumber',
  container_type: 'containerTypeCode', containertype: 'containerTypeCode', loaicontainer: 'containerTypeCode', isocode: 'containerTypeCode',
  movement_type: 'movementType', movementtype: 'movementType', nhucau: 'movementType',
  source_reference: 'sourceReference', sourcereference: 'sourceReference', mathamchieu: 'sourceReference',
  seal_number: 'sealNumber', sealnumber: 'sealNumber', soseal: 'sealNumber',
  load_status: 'loadStatus', loadstatus: 'loadStatus', tinhtranghang: 'loadStatus',
  cargo_type: 'cargoType', cargotype: 'cargoType', loaihang: 'cargoType',
  gross_weight_kg: 'grossWeightKg', grossweightkg: 'grossWeightKg', trongluongkg: 'grossWeightKg',
  vessel_call_code: 'vesselCallCode', vesselcallcode: 'vesselCallCode', machuyentau: 'vesselCallCode',
  expected_arrival_at: 'expectedArrivalAt', expectedarrivalat: 'expectedArrivalAt', thoigiandenkien: 'expectedArrivalAt',
  expected_available_at: 'expectedAvailableAt', expectedavailableat: 'expectedAvailableAt', thoigiansansangdukien: 'expectedAvailableAt',
  requested_pickup_date: 'requestedPickupDate', requestedpickupdate: 'requestedPickupDate', ngaymongmuonnhan: 'requestedPickupDate',
  bl_booking_number: 'blBookingNumber', blbookingnumber: 'blBookingNumber', soblbooking: 'blBookingNumber',
  customer_name: 'customerName', customername: 'customerName', tenkhachhang: 'customerName',
  transport_company_name: 'transportCompanyName', transportcompanyname: 'transportCompanyName', tencongtyvanchuyen: 'transportCompanyName',
};

const normalizeHeader = (value: string): string => value.trim().toLowerCase()
  .normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/g, 'd')
  .replace(/[^a-z0-9_]+/g, '');

const dateValue = (value: unknown, dateOnly = false): unknown => {
  if (typeof value === 'number') {
    const parsed = XLSX.SSF.parse_date_code(value);
    if (!parsed) return value;
    if (dateOnly) return `${parsed.y}-${String(parsed.m).padStart(2, '0')}-${String(parsed.d).padStart(2, '0')}`;
    const date = new Date(Date.UTC(parsed.y, parsed.m - 1, parsed.d, parsed.H - 7, parsed.M, Math.floor(parsed.S)));
    return date.toISOString();
  }
  if (typeof value === 'string') {
    const trimmed = value.trim();
    if (dateOnly && /^\d{4}-\d{2}-\d{2}$/.test(trimmed)) return trimmed;
    if (!dateOnly && /^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}(:\d{2})?$/.test(trimmed)) {
      return new Date(`${trimmed.replace(' ', 'T')}${trimmed.length === 16 ? ':00' : ''}+07:00`).toISOString();
    }
  }
  return value;
};

const normalizeEnum = (key: keyof ContainerIntakeRow, value: unknown): unknown => {
  if (typeof value !== 'string') return value;
  const normalized = value.trim().toLowerCase();
  if (key === 'loadStatus') return ({ day: 'full', full: 'full', rong: 'empty', empty: 'empty', unknown: 'unknown' } as Record<string, string>)[normalized] ?? normalized;
  if (key === 'cargoType' || key === 'movementType') return normalized.replace(/[ -]+/g, '_');
  return value.trim();
};

const applyIntakeDefaults = (row: ContainerIntakeRow): ContainerIntakeRow => {
  if (row.sourceType !== 'port_vessel') return row;
  const expectedAvailableAt = row.expectedArrivalAt
    ? new Date(new Date(row.expectedArrivalAt).getTime() + 12 * 60 * 60 * 1000).toISOString()
    : null;
  return {
    ...row,
    vesselCallCode: null,
    transportCompanyName: null,
    expectedAvailableAt,
  };
};

export class ContainerIntakeService {
  constructor(private readonly repository = new ContainerIntakeRepository()) {}

  async createManual(raw: unknown, sourceType: ContainerIntakeSource, userId: string) {
    const parsed = containerIntakeSchema.safeParse({ ...(raw as object), sourceType });
    if (!parsed.success) {
      const details = parsed.error.issues.reduce<Record<string, string[]>>((result, issue) => {
        const field = issue.path.join('.') || 'request';
        result[field] = [...(result[field] ?? []), issue.message];
        return result;
      }, {});
      throw new ValidationError('Thông tin tiếp nhận Container không hợp lệ.', details);
    }
    return this.repository.processRow(applyIntakeDefaults(parsed.data), userId);
  }

  async importExcel(file: Express.Multer.File | undefined, sourceType: ContainerIntakeSource, userId: string) {
    if (!file) throw new ValidationError('Vui lòng chọn file Excel cần import.');
    if (!/\.(xlsx|xls)$/i.test(file.originalname)) throw new ValidationError('Chỉ chấp nhận file Excel .xlsx hoặc .xls.');
    const batchId = await this.repository.createBatch(sourceType, file.originalname, file.size, userId);

    let workbook: XLSX.WorkBook;
    try { workbook = XLSX.read(file.buffer, { type: 'buffer', cellDates: false }); }
    catch {
      await this.repository.failBatch(batchId);
      throw new ValidationError('Không thể đọc file Excel. Vui lòng dùng đúng file mẫu.');
    }
    const preferredName = sourceType === 'port_vessel' ? 'Cang_Tau' : 'CongTyVanChuyen';
    const sheet = workbook.Sheets[preferredName] ?? workbook.Sheets[workbook.SheetNames[0]];
    if (!sheet) {
      await this.repository.failBatch(batchId);
      throw new ValidationError('File Excel không có sheet dữ liệu.');
    }
    const rawRows = XLSX.utils.sheet_to_json<Record<string, unknown>>(sheet, { defval: '', raw: true });
    if (!rawRows.length) {
      await this.repository.failBatch(batchId);
      throw new ValidationError('File Excel không có dòng dữ liệu nào.');
    }
    if (rawRows.length > 1000) {
      await this.repository.failBatch(batchId);
      throw new ValidationError('Mỗi lần chỉ được import tối đa 1.000 dòng.');
    }
    const results: ContainerIntakeResult[] = [];
    for (let index = 0; index < rawRows.length; index += 1) {
      const raw = rawRows[index];
      const mapped: Record<string, unknown> = {};
      for (const [header, value] of Object.entries(raw)) {
        const key = HEADERS[normalizeHeader(header)];
        if (key) {
          const normalizedValue = ['expectedArrivalAt', 'expectedAvailableAt', 'requestedPickupDate'].includes(key)
            ? dateValue(value, key === 'requestedPickupDate') : value;
          mapped[key] = normalizeEnum(key, normalizedValue);
        }
      }
      mapped.sourceType = sourceType;
      mapped.movementType = sourceType === 'port_vessel' ? 'vessel_discharge' : (mapped.movementType || 'pickup_request');
      const rowNumber = index + 2;
      const parsed = containerIntakeSchema.safeParse(mapped);
      let result: ContainerIntakeResult;
      if (!parsed.success) {
        result = {
          rowNumber,
          containerNumber: typeof mapped.containerNumber === 'string' ? mapped.containerNumber : null,
          status: 'rejected',
          errors: parsed.error.issues.map((issue) => `${issue.path.join('.') || 'dòng'}: ${issue.message}`),
        };
      } else {
        try { result = await this.repository.processRow(applyIntakeDefaults(parsed.data), userId, batchId, rowNumber); }
        catch (error) {
          result = {
            rowNumber, containerNumber: parsed.data.containerNumber, status: 'rejected',
            errors: [error instanceof AppError ? error.message : 'Không thể lưu dòng dữ liệu.'],
          };
        }
      }
      results.push(result);
      await this.repository.saveRowResult(batchId, result, raw);
    }
    await this.repository.completeBatch(batchId, results);
    return {
      batchId,
      totalRows: results.length,
      successRows: results.filter((row) => ['created_master', 'created_visit', 'updated_visit'].includes(row.status)).length,
      duplicateRows: results.filter((row) => row.status === 'duplicate').length,
      failedRows: results.filter((row) => row.status === 'rejected').length,
      rows: results,
    };
  }

  listImports() { return this.repository.listBatches(); }
}

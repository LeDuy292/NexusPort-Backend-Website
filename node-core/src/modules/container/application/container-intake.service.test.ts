import * as XLSX from 'xlsx';
import { ContainerIntakeRepository } from '../infrastructure/container-intake.repository';
import { ContainerIntakeService } from './container-intake.service';

const userId = '5ce5cd9b-1da8-441e-8442-04b9cb7f76c7';

const buildFile = (sheetName: string, rows: Record<string, unknown>[]) => {
  const workbook = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(workbook, XLSX.utils.json_to_sheet(rows), sheetName);
  const buffer = XLSX.write(workbook, { type: 'buffer', bookType: 'xlsx' });
  return { originalname: 'containers.xlsx', size: buffer.length, buffer } as Express.Multer.File;
};

describe('ContainerIntakeService', () => {
  it('uses the same normalized intake flow for a port Excel row', async () => {
    const repository = {
      createBatch: jest.fn().mockResolvedValue('batch-1'),
      processRow: jest.fn().mockResolvedValue({ rowNumber: 2, containerNumber: 'EMCU8361795', status: 'created_master', errors: [] }),
      saveRowResult: jest.fn().mockResolvedValue(undefined),
      completeBatch: jest.fn().mockResolvedValue(undefined),
      failBatch: jest.fn().mockResolvedValue(undefined),
      listBatches: jest.fn(),
    } as unknown as jest.Mocked<ContainerIntakeRepository>;
    const service = new ContainerIntakeService(repository);
    const file = buildFile('Cang_Tau', [{
      container_number: 'EMCU8361795', container_type: '40HC',
      expected_arrival_at: '2026-04-02 16:59', load_status: 'full', cargo_type: 'general', gross_weight_kg: 26493,
      vessel_call_code: 'LEGACY-VOYAGE', expected_available_at: '2026-04-03 18:00',
      transport_company_name: 'Không dùng cho nguồn cảng',
    }]);

    const result = await service.importExcel(file, 'port_vessel', userId);

    expect(result).toMatchObject({ totalRows: 1, successRows: 1, failedRows: 0 });
    expect(repository.processRow).toHaveBeenCalledWith(expect.objectContaining({
      containerNumber: 'EMCU8361795', containerTypeCode: '40HC', sourceType: 'port_vessel',
      movementType: 'vessel_discharge', expectedArrivalAt: '2026-04-02T09:59:00.000Z',
      expectedAvailableAt: '2026-04-02T21:59:00.000Z', vesselCallCode: null,
      transportCompanyName: null, grossWeightKg: 26493,
    }), userId, 'batch-1', 2);
  });

  it('records an invalid company declaration instead of creating master data', async () => {
    const repository = {
      createBatch: jest.fn().mockResolvedValue('batch-2'), processRow: jest.fn(),
      saveRowResult: jest.fn().mockResolvedValue(undefined), completeBatch: jest.fn().mockResolvedValue(undefined),
      failBatch: jest.fn().mockResolvedValue(undefined),
      listBatches: jest.fn(),
    } as unknown as jest.Mocked<ContainerIntakeRepository>;
    const service = new ContainerIntakeService(repository);
    const file = buildFile('CongTyVanChuyen', [{
      container_number: 'INVALID', container_type: '40HC', movement_type: 'pickup_request',
      requested_pickup_date: '2026-04-04', transport_company_name: 'Demo Logistics',
    }]);

    const result = await service.importExcel(file, 'transport_company', userId);

    expect(result.failedRows).toBe(1);
    expect(result.rows[0].status).toBe('rejected');
    expect(repository.processRow).not.toHaveBeenCalled();
    expect(repository.saveRowResult).toHaveBeenCalled();
  });

  it.each(['pickup_request', 'truck_dropoff'])('uses one requested service date for %s', async (movementType) => {
    const repository = {
      createBatch: jest.fn().mockResolvedValue('batch-3'),
      processRow: jest.fn().mockResolvedValue({ rowNumber: 2, containerNumber: 'EMCU8361795', status: 'created_visit', errors: [] }),
      saveRowResult: jest.fn().mockResolvedValue(undefined), completeBatch: jest.fn().mockResolvedValue(undefined),
      failBatch: jest.fn().mockResolvedValue(undefined), listBatches: jest.fn(),
    } as unknown as jest.Mocked<ContainerIntakeRepository>;
    const service = new ContainerIntakeService(repository);
    const file = buildFile('CongTyVanChuyen', [{
      container_number: 'EMCU8361795', container_type: '40HC', movement_type: movementType,
      requested_service_date: '2026-04-04', transport_company_name: 'Demo Logistics',
      bl_booking_number: 'EXT-BOOKING-01',
    }]);

    const result = await service.importExcel(file, 'transport_company', userId);

    expect(result).toMatchObject({ successRows: 1, failedRows: 0 });
    expect(repository.processRow).toHaveBeenCalledWith(expect.objectContaining({
      movementType, requestedServiceDate: '2026-04-04', blBookingNumber: 'EXT-BOOKING-01',
    }), userId, 'batch-3', 2);
  });

  it('requires a requested service date for every transport movement', async () => {
    const repository = {
      createBatch: jest.fn().mockResolvedValue('batch-4'), processRow: jest.fn(),
      saveRowResult: jest.fn().mockResolvedValue(undefined), completeBatch: jest.fn().mockResolvedValue(undefined),
      failBatch: jest.fn().mockResolvedValue(undefined), listBatches: jest.fn(),
    } as unknown as jest.Mocked<ContainerIntakeRepository>;
    const service = new ContainerIntakeService(repository);
    const file = buildFile('CongTyVanChuyen', [{
      container_number: 'EMCU8361795', container_type: '40HC', movement_type: 'truck_dropoff',
      transport_company_name: 'Demo Logistics',
    }]);

    const result = await service.importExcel(file, 'transport_company', userId);

    expect(result.failedRows).toBe(1);
    expect(result.rows[0].errors.join(' ')).toContain('requestedServiceDate');
    expect(repository.processRow).not.toHaveBeenCalled();
  });
});

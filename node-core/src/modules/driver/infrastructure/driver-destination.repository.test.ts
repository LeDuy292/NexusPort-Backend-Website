import { query } from '../../../infrastructure/database/db';
import { DriverDestinationRepository } from './driver-destination.repository';

jest.mock('../../../infrastructure/database/db', () => ({ query: jest.fn() }));
const mockedQuery = query as jest.MockedFunction<typeof query>;

describe('DriverDestinationRepository', () => {
  beforeEach(() => mockedQuery.mockReset());

  it('returns Operation-linked destinations for only the authenticated Driver', async () => {
    const assignedAt = new Date('2026-10-06T10:00:00Z');
    mockedQuery.mockResolvedValue({ rows: [{
      operationId: 'operation-1', taskCode: 'YT-001', operationType: 'Unload', operationStatus: 'Assigned',
      containerId: 'container-1', containerNumber: 'CSQU3054383', driverId: 'driver-1', assignedAt,
      assignedBy: 'dispatcher01', slotId: 'slot-1', blockId: 'block-1', block: 'A01', blockName: 'Block A01',
      zone: 'North', bay: 5, row: 2, tier: 3, status: 'reserved', maxWeightKg: '35000', hasReeferPlug: false,
    }] } as Awaited<ReturnType<typeof query>>);

    const result = await new DriverDestinationRepository().findForDriver('driver-1');

    expect(mockedQuery).toHaveBeenCalledWith(expect.stringContaining('yt."DriverId" = $1'), ['driver-1']);
    expect(result[0]).toMatchObject({
      operationId: 'operation-1',
      destination: { block: 'A01', bay: 5, row: 2, tier: 3, maxWeightKg: 35000 },
    });
    expect(mockedQuery.mock.calls[0][0]).toContain("ysr.reason = 'NXP-059 operation:' || yt.\"Id\"::text");
  });
});

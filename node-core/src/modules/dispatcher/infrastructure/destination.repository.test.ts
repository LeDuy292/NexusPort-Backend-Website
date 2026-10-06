import { getClient } from '../../../infrastructure/database/db';
import { DestinationRepository } from './destination.repository';

jest.mock('../../../infrastructure/database/db', () => ({ query: jest.fn(), getClient: jest.fn() }));
const mockedGetClient = getClient as jest.MockedFunction<typeof getClient>;

const operationId = '11111111-1111-4111-8111-111111111111';
const containerId = '22222222-2222-4222-8222-222222222222';
const driverId = '33333333-3333-4333-8333-333333333333';
const slotId = '44444444-4444-4444-8444-444444444444';

const assignedRow = {
  operationId, taskCode: 'YT-001', operationType: 'Unload', operationStatus: 'Assigned',
  containerId, containerNumber: 'CSQU3054383', driverId, assignedAt: new Date('2026-10-06T10:00:00Z'),
  assignedBy: 'dispatcher01', slotId, blockId: '55555555-5555-4555-8555-555555555555',
  block: 'A01', blockName: 'Block A01', zone: 'North', bay: 5, row: 2, tier: 3,
  status: 'reserved', maxWeightKg: '35000', hasReeferPlug: false,
};

describe('DestinationRepository assignment transaction', () => {
  it('reserves the selected slot, links it to the Operation and commits', async () => {
    const client = {
      query: jest.fn(async (sql: string) => {
        if (sql.includes('FROM yard_tasks') && sql.includes('FOR UPDATE')) {
          return { rows: [{ operationId, containerId, driverId, operationStatus: 'Assigned' }] };
        }
        if (sql.includes('FROM yard_slots ys') && sql.includes('FOR UPDATE OF ys')) {
          return { rows: [{
            slotId, blockId: assignedRow.blockId, block: 'A01', blockName: 'Block A01', zone: 'North',
            bay: 5, row: 2, tier: 3, status: 'empty', maxWeightKg: '35000', hasReeferPlug: false,
          }] };
        }
        if (sql.includes('FROM yard_slot_reservations') && sql.includes('LIMIT 1 FOR UPDATE')) return { rows: [] };
        if (sql.includes('UPDATE yard_slot_reservations')) return { rows: [] };
        if (sql.includes('FROM yard_tasks yt')) return { rows: [assignedRow] };
        return { rows: [] };
      }),
      release: jest.fn(),
    };
    mockedGetClient.mockResolvedValue(client as never);

    const result = await new DestinationRepository().assign({
      operationId, slotId, dispatcherId: null, dispatcherName: 'dispatcher01',
    });

    expect(result.destination).toMatchObject({ slotId, block: 'A01', bay: 5, row: 2, tier: 3 });
    expect(client.query).toHaveBeenCalledWith(expect.stringContaining('INSERT INTO yard_slot_reservations'),
      [slotId, containerId, null, `NXP-059 operation:${operationId}`]);
    expect(client.query).toHaveBeenCalledWith('COMMIT');
    expect(client.release).toHaveBeenCalled();
  });

  it('rolls back when another assignment already holds the slot', async () => {
    const client = {
      query: jest.fn(async (sql: string) => {
        if (sql.includes('FROM yard_tasks') && sql.includes('FOR UPDATE')) {
          return { rows: [{ operationId, containerId, driverId, operationStatus: 'Assigned' }] };
        }
        if (sql.includes('FROM yard_slots ys')) return { rows: [{ ...assignedRow, status: 'reserved' }] };
        if (sql.includes('FROM yard_slot_reservations')) {
          return { rows: [{ containerId: '66666666-6666-4666-8666-666666666666', reason: 'other' }] };
        }
        return { rows: [] };
      }),
      release: jest.fn(),
    };
    mockedGetClient.mockResolvedValue(client as never);

    await expect(new DestinationRepository().assign({
      operationId, slotId, dispatcherId: null, dispatcherName: 'dispatcher01',
    })).rejects.toMatchObject({ statusCode: 409, errorCode: 'CONFLICT' });

    expect(client.query).toHaveBeenCalledWith('ROLLBACK');
    expect(client.query).not.toHaveBeenCalledWith(expect.stringContaining('INSERT INTO yard_slot_reservations'), expect.anything());
    expect(client.release).toHaveBeenCalled();
  });
});

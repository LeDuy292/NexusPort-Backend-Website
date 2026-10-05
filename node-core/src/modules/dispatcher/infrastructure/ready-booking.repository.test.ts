import { query } from '../../../infrastructure/database/db';
import { ReadyBookingRepository } from './ready-booking.repository';

jest.mock('../../../infrastructure/database/db', () => ({ query: jest.fn() }));

const mockedQuery = query as jest.MockedFunction<typeof query>;

describe('ReadyBookingRepository', () => {
  beforeEach(() => mockedQuery.mockReset());

  it('groups containers by approved booking and exposes its driver and vehicle', async () => {
    const row = {
      id: 'booking-1', bookingCode: 'B-001', bookingType: 'Pickup', status: 'Approved',
      appointmentStart: new Date('2026-09-16T00:00:00Z'),
      appointmentEnd: new Date('2026-09-16T01:00:00Z'),
      driverId: 'driver-1', driverName: 'Nguyen Van A', driverStatus: 'active',
      vehicleId: 'truck-1', vehiclePlate: '51A-12345', vehicleStatus: 'active',
      containerId: 'container-1', containerNumber: 'ABCD1234567', containerStatus: 'expected',
    };
    mockedQuery.mockResolvedValue({ rows: [row, {
      ...row, containerId: 'container-2', containerNumber: 'EFGH1234567',
    }] } as Awaited<ReturnType<typeof query>>);

    const result = await new ReadyBookingRepository().findAll();

    expect(result).toHaveLength(1);
    expect(result[0].driver).toEqual({ id: 'driver-1', name: 'Nguyen Van A', status: 'active' });
    expect(result[0].vehicle).toEqual({ id: 'truck-1', name: '51A-12345', status: 'active' });
    expect(result[0].containers.map(item => item.id)).toEqual(['container-1', 'container-2']);
    expect(mockedQuery.mock.calls[0][0]).toContain("lower(b.status::text) = 'approved'");
  });

  it('returns null for an unassigned driver or vehicle', async () => {
    mockedQuery.mockResolvedValue({ rows: [{
      id: 'booking-2', bookingCode: 'B-002', bookingType: 'Delivery', status: 'Approved',
      appointmentStart: new Date(), appointmentEnd: new Date(),
      driverId: null, driverName: null, driverStatus: null,
      vehicleId: null, vehiclePlate: null, vehicleStatus: null,
      containerId: 'container-3', containerNumber: 'IJKL1234567', containerStatus: 'expected',
    }] } as Awaited<ReturnType<typeof query>>);

    const result = await new ReadyBookingRepository().findAll();

    expect(result[0].driver).toBeNull();
    expect(result[0].vehicle).toBeNull();
  });
});

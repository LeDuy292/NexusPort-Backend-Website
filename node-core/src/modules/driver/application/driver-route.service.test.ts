import { DriverRouteService } from './driver-route.service';
import { DriverRouteRepository, DriverRouteRow } from '../infrastructure/driver-route.repository';

const row: DriverRouteRow = {
  containerId: '11111111-1111-4111-8111-111111111111',
  containerNumber: 'CSQU3054383',
  bookingId: '22222222-2222-4222-8222-222222222222',
  gateTransactionId: '33333333-3333-4333-8333-333333333333',
  gateStatus: 'allowed', gateCode: 'G1',
  slotId: '44444444-4444-4444-8444-444444444444',
  zone: 'North', blockCode: 'A01', bay: 5, row: 2, tier: 3,
  instructionId: null, fromGate: null, routeDetail: null, mapData: null,
};

describe('Driver route after Gate-In PASS', () => {
  const driverId = '55555555-5555-4555-8555-555555555555';

  it('returns destination and directions linked to the exact container', async () => {
    const repository = {
      findForDriverAndContainer: jest.fn().mockResolvedValue(row),
    } as unknown as DriverRouteRepository;
    const route = await new DriverRouteService(repository).get(driverId, row.containerId);

    expect(repository.findForDriverAndContainer).toHaveBeenCalledWith(driverId, row.containerId);
    expect(route.containerId).toBe(row.containerId);
    expect(route.destination).toEqual({ slotId: row.slotId, zone: 'North', block: 'A01', bay: 5, row: 2, tier: 3 });
    expect(route.route?.fromGate).toBe('G1');
    expect(route.route?.directions).toEqual(expect.arrayContaining([
      expect.stringContaining('Block A01'),
      expect.stringContaining('Bay 5, Row 2'),
    ]));
  });

  it('does not invent a destination before a Yard slot is assigned', async () => {
    const repository = {
      findForDriverAndContainer: jest.fn().mockResolvedValue({ ...row, slotId: null }),
    } as unknown as DriverRouteRepository;
    await expect(new DriverRouteService(repository).get(driverId, row.containerId))
      .rejects.toMatchObject({ statusCode: 409, errorCode: 'YARD_LOCATION_NOT_ASSIGNED' });
  });

  it('does not expose a container without a passed Gate-In for that driver', async () => {
    const repository = { findForDriverAndContainer: jest.fn().mockResolvedValue(null) } as unknown as DriverRouteRepository;
    await expect(new DriverRouteService(repository).get(driverId, row.containerId))
      .rejects.toMatchObject({ statusCode: 404, errorCode: 'DRIVER_ROUTE_NOT_FOUND' });
  });
});

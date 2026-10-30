import { SmartRouteService } from './smart-route.service';
import { shortestRoute, YardMap, yardMapSchema, RouteTarget } from '../domain/smart-route';
import { DriverRouteRow } from '../infrastructure/driver-route.repository';
import { DriverRouteService } from './driver-route.service';
import { DriverRouteRepository } from '../infrastructure/driver-route.repository';
import { RabbitMQClient } from '../../../infrastructure/clients/rabbitmq';
import { dispatcherStatusUpdatedSchema } from '../../../realtime/driver-realtime.gateway';

const mapInput = {
  version: 'test-1',
  nodes: [
    { id: 'gate', label: 'Gate G1', x: 0, y: 0, gateCode: 'G1' },
    { id: 'direct', label: 'Đường chính', x: 50, y: 0 },
    { id: 'bypass', label: 'Đường vòng', x: 0, y: 80 },
    { id: 'block', label: 'Block A01', x: 100, y: 80, blockCode: 'A01' },
    { id: 'inspection', label: 'Khu kiểm hóa', x: 150, y: 80, location: 'INSPECTION' },
  ],
  edges: [
    { id: 'g-d', from: 'gate', to: 'direct', distanceMeters: 50, travelSeconds: 10 },
    { id: 'd-b', from: 'direct', to: 'block', distanceMeters: 100, travelSeconds: 10 },
    { id: 'g-v', from: 'gate', to: 'bypass', distanceMeters: 80, travelSeconds: 20 },
    { id: 'v-b', from: 'bypass', to: 'block', distanceMeters: 100, travelSeconds: 20 },
    { id: 'b-i', from: 'block', to: 'inspection', distanceMeters: 50, travelSeconds: 10 },
  ],
};
const row: DriverRouteRow = {
  containerId: '11111111-1111-4111-8111-111111111111', containerNumber: 'CSQU3054383',
  bookingId: '22222222-2222-4222-8222-222222222222',
  gateTransactionId: '33333333-3333-4333-8333-333333333333',
  gateStatus: 'allowed', gateCode: 'G1', slotId: '44444444-4444-4444-8444-444444444444',
  zone: 'North', blockCode: 'A01', bay: 5, row: 2, tier: 3,
  instructionId: null, fromGate: 'STALE-GATE', routeDetail: null, mapData: null,
  operationId: '55555555-5555-4555-8555-555555555555', targetLocation: 'A01-05-02-3', destinationSource: 'operation',
};
const target: RouteTarget = {
  type: 'slot', location: 'A01-05-02-3', slotId: row.slotId!, block: 'A01', zone: 'North',
  bay: 5, row: 2, tier: 3, source: 'operation',
};
const serviceFor = (map: YardMap = yardMapSchema.parse(mapInput)) => new SmartRouteService({ load: async () => ({ map }) });

describe('NXP-114 Smart Route', () => {
  it('starts at the actual passed gate and reaches the assigned block and slot', async () => {
    const route = await serviceFor().create(row, target);
    expect(route.status).toBe('available');
    expect(route.waypoints.map(point => point.id)).toEqual(['gate', 'direct', 'block']);
    expect(route.distanceMeters).toBe(150);
    expect(route.estimatedSeconds).toBe(20);
    expect(route.directions.at(-1)).toContain('Bay 5, Row 2');
    expect(route.directions.at(-1)).toContain('Tier 3');
  });

  it.each(['restriction', 'congestion'])('chooses a bypass for %s', async condition => {
    const map = yardMapSchema.parse(mapInput);
    if (condition === 'restriction') map.edges[0].restricted = true;
    else map.edges[0].congestion = 1;
    const route = await serviceFor(map).create(row, target);
    expect(route.waypoints.map(point => point.id)).toEqual(['gate', 'bypass', 'block']);
    expect(route.rerouted).toBe(true);
    expect(route.distanceMeters).toBe(180);
  });

  it('blocks paths through a restricted area even when its roads are open', async () => {
    const map = yardMapSchema.parse(mapInput);
    map.nodes[1].restricted = true;
    expect((await serviceFor(map).create(row, target)).waypoints.map(point => point.id)).toEqual(['gate', 'bypass', 'block']);
  });

  it('returns no safe route rather than directions across closed roads', async () => {
    const map = yardMapSchema.parse(mapInput);
    map.edges[0].restricted = true;
    map.edges[2].restricted = true;
    const route = await serviceFor(map).create(row, target);
    expect(route).toMatchObject({ status: 'unavailable', reasonCode: 'NO_SAFE_ROUTE', directions: [], waypoints: [] });
  });

  it('respects one-way roads and explicitly configured bidirectional roads', () => {
    const map = yardMapSchema.parse(mapInput);
    expect(shortestRoute(map, 'block', 'gate')).toBeNull();
    map.edges[0].bidirectional = true;
    map.edges[1].bidirectional = true;
    expect(shortestRoute(map, 'block', 'gate')?.waypoints.map(point => point.id)).toEqual(['block', 'direct', 'gate']);
  });

  it('recalculates from the driver-confirmed current waypoint', async () => {
    const route = await serviceFor().create(row, target, 'direct');
    expect(route.waypoints.map(point => point.id)).toEqual(['direct', 'block']);
    expect(route.distanceMeters).toBe(100);
    expect((await serviceFor().create(row, target, 'unknown')).reasonCode).toBe('CURRENT_POSITION_NOT_MAPPED');
  });

  it('supports assigned processing areas without inventing a Yard slot', async () => {
    const area: RouteTarget = { ...target, type: 'area', location: 'INSPECTION', slotId: null, block: null, bay: null, row: null, tier: null };
    const route = await serviceFor().create(row, area);
    expect(route.destinationNode?.id).toBe('inspection');
    expect(route.directions.at(-1)).toContain('INSPECTION');
  });

  it('does not fall back to a block when an area destination is unmapped', async () => {
    expect((await serviceFor().create(row, { ...target, type: 'area', location: 'UNKNOWN' })).reasonCode).toBe('DESTINATION_NOT_MAPPED');
  });

  it('supports an Operation assigned to an entire block', async () => {
    const route = await serviceFor().create(row, { ...target, type: 'area', location: 'A01', slotId: null });
    expect(route.status).toBe('available');
    expect(route.destinationNode?.id).toBe('block');
  });

  it('does not treat a nonexistent Yard slot as a processing area', async () => {
    expect((await serviceFor().create(row, { ...target, type: 'area', location: 'A01-999-2-3', slotId: null })).reasonCode).toBe('YARD_LOCATION_INVALID');
  });

  it('refuses maintenance slots and restricted destination nodes', async () => {
    expect((await serviceFor().create({ ...row, slotStatus: 'maintenance' }, target)).reasonCode).toBe('DESTINATION_RESTRICTED');
    const map = yardMapSchema.parse(mapInput);
    map.nodes[3].restricted = true;
    expect((await serviceFor(map).create(row, target)).reasonCode).toBe('DESTINATION_RESTRICTED');
  });

  it('keeps a stable revision until destination or route conditions change', async () => {
    const service = serviceFor();
    const initial = await service.create(row, target);
    expect((await service.create(row, target)).revision).toBe(initial.revision);
    expect((await service.create(row, { ...target, tier: 4 })).revision).not.toBe(initial.revision);
    const map = yardMapSchema.parse(mapInput);
    map.edges[0].congestion = 0.2;
    expect((await serviceFor(map).create(row, target)).revision).not.toBe(initial.revision);
  });

  it('reports a missing map and pending destination explicitly', async () => {
    const service = new SmartRouteService({ load: async () => ({ map: null, errorCode: 'YARD_MAP_NOT_CONFIGURED' }) });
    expect((await service.create(row, target)).reasonCode).toBe('YARD_MAP_NOT_CONFIGURED');
    expect((await service.create(row, null)).status).toBe('pending_destination');
  });

  it('shows the approved yard map and gate marker while a destination is pending', async () => {
    const result = await serviceFor().create(row, null);
    expect(result).toMatchObject({ status: 'pending_destination', reasonCode: 'YARD_LOCATION_NOT_ASSIGNED', origin: { id: 'gate' } });
    expect(result.map?.nodes.length).toBe(mapInput.nodes.length);
    expect(result.waypoints).toEqual([]);
  });

  it('uses an explicit demo reception assignment without fabricating cargo completion', async () => {
    const assigned = { ...row, slotId: null, operationId: null, targetLocation: 'INSPECTION', destinationSource: 'demo_admission' as const };
    const repository = { findForDriverAndContainer: jest.fn().mockResolvedValue(assigned) } as unknown as DriverRouteRepository;
    const route = await new DriverRouteService(repository, serviceFor()).get('driver-id', row.containerId);
    expect(route.target).toMatchObject({ type: 'area', location: 'INSPECTION', source: 'demo_admission' });
    expect(route.navigation?.status).toBe('available');
    expect(route.journey).toMatchObject({ phase: 'to_yard', canArrive: true, canConfirmContainer: false });
    expect(route.destination).toBeNull();
  });

  it('publishes driver arrival using the event contract accepted by the mobile realtime gateway', async () => {
    const publish = jest.fn().mockResolvedValue(undefined);
    const broker = jest.spyOn(RabbitMQClient, 'getInstance').mockReturnValue({ publish } as unknown as RabbitMQClient);
    try {
      const repository = { findForDriverAndContainer: jest.fn().mockResolvedValue(row), arrive: jest.fn() } as unknown as DriverRouteRepository;
      await new DriverRouteService(repository, serviceFor()).arrive(row.containerId, row.containerId, { bookingId: row.bookingId, action: 'arrive' });
      expect(repository.arrive).toHaveBeenCalled();
      const [topic, event] = publish.mock.calls[0];
      expect(topic).toBe('dispatcher.status.updated');
      expect(dispatcherStatusUpdatedSchema.safeParse(event).success).toBe(true);
      expect(event).toMatchObject({ domain: 'driver', driverId: row.containerId, bookingId: row.bookingId, status: 'arrived' });
    } finally { broker.mockRestore(); }
  });

  it('preserves legacy route fields and returns the updated Operation destination each time', async () => {
    const repository = { findForDriverAndContainer: jest.fn().mockResolvedValueOnce(row).mockResolvedValueOnce({ ...row, bay: 8, targetLocation: 'A01-08-02-3' }) } as unknown as DriverRouteRepository;
    const service = new DriverRouteService(repository, serviceFor());
    const first = await service.get('driver-id', row.containerId);
    const updated = await service.get('driver-id', row.containerId);
    expect(first.route?.fromGate).toBe('G1');
    expect(first.operation?.id).toBe(row.operationId);
    expect(first.destination?.bay).toBe(5);
    expect(updated.destination?.bay).toBe(8);
    expect(updated.navigation?.revision).not.toBe(first.navigation?.revision);
  });

  it('rejects duplicate identifiers and references to missing map nodes', () => {
    expect(yardMapSchema.safeParse({ ...mapInput, nodes: [mapInput.nodes[0], mapInput.nodes[0]] }).success).toBe(false);
    expect(yardMapSchema.safeParse({ ...mapInput, edges: [{ ...mapInput.edges[0], to: 'missing' }] }).success).toBe(false);
  });
});

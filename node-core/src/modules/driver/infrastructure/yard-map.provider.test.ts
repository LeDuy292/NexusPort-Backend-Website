import { ConfiguredYardMapProvider } from './yard-map.provider';

describe('Smart Yard map provider', () => {
  const map = { version: '1', nodes: [{ id: 'gate', label: 'Gate', x: 0, y: 0 }], edges: [] };
  const previousPath = process.env.SMART_ROUTE_MAP_PATH;
  beforeEach(() => { delete process.env.SMART_ROUTE_MAP_PATH; });
  afterAll(() => {
    if (previousPath === undefined) delete process.env.SMART_ROUTE_MAP_PATH;
    else process.env.SMART_ROUTE_MAP_PATH = previousPath;
  });

  it('loads map_data.smartYardMap without changing the database schema', async () => {
    expect((await new ConfiguredYardMapProvider().load({ smartYardMap: map })).map?.version).toBe('1');
  });
  it('does not interpret legacy map data as a navigable graph', async () => {
    expect(await new ConfiguredYardMapProvider().load({ containerId: 'legacy' })).toEqual({ map: null, errorCode: 'YARD_MAP_NOT_CONFIGURED' });
  });
  it('rejects malformed and expired snapshots', async () => {
    const provider = new ConfiguredYardMapProvider();
    expect((await provider.load({ smartYardMap: { version: 'broken' } })).errorCode).toBe('YARD_MAP_INVALID');
    expect((await provider.load({ smartYardMap: { ...map, validUntil: '2020-01-01T00:00:00Z' } })).errorCode).toBe('YARD_MAP_EXPIRED');
  });
  it('fails closed when the configured live source cannot be read', async () => {
    process.env.SMART_ROUTE_MAP_PATH = 'missing-smart-yard-map.json';
    expect((await new ConfiguredYardMapProvider().load({ smartYardMap: map })).errorCode).toBe('YARD_MAP_INVALID');
  });
});

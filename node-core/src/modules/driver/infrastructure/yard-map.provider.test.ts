import { ConfiguredYardMapProvider } from './yard-map.provider';

describe('Smart Yard map provider', () => {
  const map = { version: '1', nodes: [{ id: 'gate', label: 'Gate', x: 0, y: 0 }], edges: [] };
  const previousPath = process.env.SMART_ROUTE_MAP_PATH;
  const read = jest.fn();
  const provider = () => new ConfiguredYardMapProvider(read);
  beforeEach(() => { delete process.env.SMART_ROUTE_MAP_PATH; read.mockReset().mockResolvedValue({ rows: [] }); });
  afterAll(() => {
    if (previousPath === undefined) delete process.env.SMART_ROUTE_MAP_PATH;
    else process.env.SMART_ROUTE_MAP_PATH = previousPath;
  });

  it('retains the instruction snapshot when no active DB map exists', async () => {
    expect((await provider().load({ smartYardMap: map })).map?.version).toBe('1');
  });
  it('does not interpret legacy map data as a navigable graph', async () => {
    expect(await provider().load({ containerId: 'legacy' })).toEqual({ map: null, errorCode: 'YARD_MAP_NOT_CONFIGURED' });
  });
  it('rejects malformed and expired snapshots', async () => {
    expect((await provider().load({ smartYardMap: { version: 'broken' } })).errorCode).toBe('YARD_MAP_INVALID');
    expect((await provider().load({ smartYardMap: { ...map, validUntil: '2020-01-01T00:00:00Z' } })).errorCode).toBe('YARD_MAP_EXPIRED');
  });
  it('fails closed when the configured live source cannot be read', async () => {
    process.env.SMART_ROUTE_MAP_PATH = 'missing-smart-yard-map.json';
    expect((await provider().load({ smartYardMap: map })).errorCode).toBe('YARD_MAP_INVALID');
    expect(read).not.toHaveBeenCalled();
  });
  it('loads canonical DB topology ahead of an older instruction and applies live overlays', async () => {
    const slotId='11111111-1111-4111-8111-111111111111';
    read.mockImplementation(async (sql: string) => {
      if (sql.includes('FROM smart_yard_maps')) return { rows: [{ id: 'map-id', map_data: {
        version: 'live', nodes: [{ id: 'gate', label: 'Gate', x: 0, y: 0 },{ id:'service',label:'Service',x:1,y:1 }],
        edges:[{id:'road',from:'gate',to:'service',distanceMeters:10,travelSeconds:5}],
        layout:{ blocks:[{code:'A',label:'A',x:0,y:0,width:10,height:10,bays:1,rows:1,tiers:1}],slots:[] },
      } }] };
      if (sql.includes('FROM smart_yard_conditions')) return {rows:[
        {target_type:'edge',target_id:'road',restricted:false,congestion:'0.8',observed_at:'2026-10-17T10:00:00Z'},
        {target_type:'node',target_id:'service',restricted:true,congestion:'0',observed_at:'2026-10-17T10:00:01Z'},
      ]};
      return {rows:[{id:slotId,block:'A',bay:1,row:1,tier:1,status:'occupied',containerId:null}]};
    });
    const result=(await provider().load({smartYardMap:map})).map;
    expect(result?.version).toBe('live');
    expect(result?.edges[0].congestion).toBe(0.8);
    expect(result?.nodes[1].restricted).toBe(true);
    expect(result?.layout?.slots[0]).toMatchObject({id:slotId,status:'occupied'});
    expect(read.mock.calls.find(([sql])=>sql.includes('FROM smart_yard_conditions'))?.[0]).toContain('valid_until>now()');
  });
  it('fails closed when the active map source cannot be read', async () => {
    read.mockRejectedValue(new Error('DB unavailable'));
    expect((await provider().load({smartYardMap:map})).errorCode).toBe('YARD_MAP_INVALID');
  });
});

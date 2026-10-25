import {buildExcelYardMap} from './build-yard-map';
import drawing from './reference-layout.json';
import {shortestRoute} from '../../modules/driver/domain/smart-route';

describe('Excel yard road graph',()=>{
 const slots=[{id:'11111111-1111-4111-8111-111111111111',block:'A',bay:1,row:6,tier:1,status:'empty'},{id:'22222222-2222-4222-8222-222222222222',block:'B',bay:11,row:3,tier:1,status:'reserved'},{id:'33333333-3333-4333-8333-333333333333',block:'D',bay:11,row:1,tier:1,status:'empty'}];
 const map=buildExcelYardMap(slots);
 it('has exactly one entrance and exit and preserves known slot IDs',()=>{
  expect(map.nodes.filter(n=>n.gateCode).map(n=>n.gateCode)).toEqual(['GATE-01','GATE-02']);
  expect(map.layout?.slots).toEqual(slots);
  expect(map.layout?.drawingVersion).toBe(drawing.version);
 });
 it('routes to every handover and from every handover to the exit',()=>{
  for(const target of map.nodes.filter(n=>n.slotId||n.location?.startsWith('A-S'))){
   expect(shortestRoute(map,'gate:GATE-01',target.id)).not.toBeNull();
   expect(shortestRoute(map,target.id,'gate:GATE-02')).not.toBeNull();
  }
  expect(shortestRoute(map,'gate:GATE-02','gate:GATE-01')).toBeNull();
 });
 it('does not route any segment through a source stack',()=>{
  const byId=new Map(map.nodes.map(n=>[n.id,n]));
  for(const e of map.edges){const a=byId.get(e.from)!,b=byId.get(e.to)!;
   expect(a.x===b.x||a.y===b.y).toBe(true);
   for(const area of drawing.areas){
    const hit=a.x===b.x?a.x>area.x&&a.x<area.x+area.width&&Math.min(a.y,b.y)<area.y+area.height&&Math.max(a.y,b.y)>area.y:a.y>area.y&&a.y<area.y+area.height&&Math.min(a.x,b.x)<area.x+area.width&&Math.max(a.x,b.x)>area.x;
    expect(hit).toBe(false);
   }
  }
 });
 it('keeps unknown operational blocks visible in inventory without inventing a road',()=>{
  const unknown={...slots[0],block:'R'};const candidate=buildExcelYardMap([unknown]);
  expect(candidate.layout?.slots).toEqual([unknown]);
  expect(candidate.nodes.some(n=>n.slotId===unknown.id)).toBe(false);
 });
});

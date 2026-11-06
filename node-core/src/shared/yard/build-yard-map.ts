import drawing from './reference-layout.json';
import { YardMap, yardMapSchema } from '../../modules/driver/domain/smart-route';

export const EXCEL_MAP_VERSION = 'NXP-yard-excel-v3';
type Slot = NonNullable<YardMap['layout']>['slots'][number];

/** Build aisle approaches, never a truck route through a container position. */
export function buildExcelYardMap(slots: Slot[], updatedAt = new Date().toISOString()): YardMap {
  const nodes: Array<YardMap['nodes'][number]> = [];
  const edges: Array<YardMap['edges'][number]> = [];
  const addNode = (id: string, x: number, y: number, label: string, extra: Partial<YardMap['nodes'][number]> = {}) => {
    const node = { id, x, y, label, restricted: false, ...extra };
    nodes.push(node); return node;
  };
  const addEdge = (id: string, from: string, to: string, bidirectional = false) => {
    const a=nodes.find(n=>n.id===from)!, b=nodes.find(n=>n.id===to)!;
    const distance=Math.max(1,Math.round(Math.hypot(a.x-b.x,a.y-b.y)/2));
    edges.push({id,from,to,bidirectional,distanceMeters:distance,travelSeconds:Math.max(5,Math.round(distance/3)),restricted:false,congestion:0});
  };
  const crossYs=[100,...drawing.roads.filter(r=>r.id.startsWith('cross-')).map(r=>r.points[0][1]),962];
  const mainYs=new Set(crossYs), westYs=new Set(crossYs);
  const approaches = drawing.areas.map(area=>({area,y:Number((area.y+area.height/2).toFixed(2))}));
  for(const {area,y} of approaches) if(area.zone==='A')westYs.add(y); else if(area.zone!=='D')mainYs.add(y);
  const serviceArea=(bay:number)=>approaches.find(v=>v.area.zone==='A'&&v.area.bay===bay)!;
  const dock1=serviceArea(5),dock2=serviceArea(12);
  const laneId=(lane:string,y:number)=>`${lane}:${y}`;
  for(const [lane,x,ys]of [['west',300,westYs],['main',691,mainYs]] as const){
    const sorted=[...ys].sort((a,b)=>a-b);
    sorted.forEach(y=>addNode(laneId(lane,y),x,y,`${lane==='west'?'Lối phía Tây':'Đường vào bãi'} ${y}`));
    for(let i=1;i<sorted.length;i++)addEdge(`${lane}:${sorted[i-1]}:${sorted[i]}`,laneId(lane,sorted[i-1]),laneId(lane,sorted[i]),lane==='west');
  }
  for(const y of crossYs)addEdge(`cross:${y}`,laneId('west',y),laneId('main',y),true);
  addNode('gate:GATE-01',691,90,'Cổng vào',{gateCode:'GATE-01',location:'GATE-01'});
  addNode('gate:GATE-02',949,90,'Cổng ra',{gateCode:'GATE-02',location:'GATE-02'});
  addNode('east-return',949,962,'Đường ra bãi');
  addEdge('access:GATE-01','gate:GATE-01',laneId('main',100));
  addEdge('access:GATE-02','east-return','gate:GATE-02');
  for(const [id,location,dock] of [['area-a-s1','A-S1',dock1],['area-a-s2','A-S2',dock2]] as const){
    addNode(id,270,dock.y,`${location} · Điểm giao nhận`,{location});
    addEdge(`access:${location}`,laneId('west',dock.y),id,true);
  }
  const positions=new Map(approaches.map(({area,y})=>[`${area.zone}:${area.bay}`,{area,y}]));
  const knownBlocks=new Set<string>();
  for(const slot of slots){
    const match=positions.get(`${slot.block}:${slot.bay}`);
    if(!match)continue;
    const {area,y}=match;
    let lane:string, x:number, accessY:number;
    if(area.zone==='A'){lane=laneId('west',y);x=270;accessY=y;}
    else if(area.zone==='D'){
      x=area.x+area.width/2;accessY=970;lane=`d-approach:${area.code}`;
      if(!nodes.some(n=>n.id===lane)){
        addNode(lane,x,962,`${area.code} · Lối tiếp cận`);
        // The whole south cross-aisle is clear of the D stacks.
        if(x<=691)addEdge(`d-access:${area.code}`,laneId('main',962),lane,true);
      }
    }else{lane=laneId('main',y);x=area.zone==='B'?665:725;accessY=y;}
    const id=`slot:${slot.id}`;
    addNode(id,x,accessY,`${slot.block}-${slot.bay}-${slot.row}-${slot.tier} · Điểm giao nhận`,{slotId:slot.id,blockCode:slot.block,location:`${slot.block}-${slot.bay}-${slot.row}-${slot.tier}`});
    addEdge(`access:slot:${slot.id}`,lane,id,true);
    if(!knownBlocks.has(slot.block)){
      knownBlocks.add(slot.block);const blockId=`block:${slot.block}`;
      addNode(blockId,x,accessY,`Khu ${slot.block} · Điểm giao nhận`,{blockCode:slot.block});
      addEdge(`access:block:${slot.block}`,lane,blockId,true);
    }
  }
  const eastJunctions=nodes.filter(n=>n.id.startsWith('d-approach:')&&n.x>691).sort((a,b)=>a.x-b.x);
  const returnNodes=[laneId('main',962),...eastJunctions.map(n=>n.id),'east-return'];
  for(let i=1;i<returnNodes.length;i++)addEdge(`south-return:${i}`,returnNodes[i-1],returnNodes[i]);
  // Unknown existing blocks stay outside the new drawing and remain explicitly
  // unmapped. Publishing never moves containers or changes slot IDs/capacity.
  const blocks=[...knownBlocks].map(code=>{
    const areas=drawing.areas.filter(a=>a.zone===code), group=slots.filter(s=>s.block===code);
    const x=Math.min(...areas.map(a=>a.x)),y=Math.min(...areas.map(a=>a.y));
    return {code,label:`Khu ${code}`,x,y,width:Math.max(...areas.map(a=>a.x+a.width))-x,height:Math.max(...areas.map(a=>a.y+a.height))-y,bays:Math.max(...group.map(s=>s.bay)),rows:Math.max(...group.map(s=>s.row)),tiers:Math.max(...group.map(s=>s.tier))};
  });
  const map=yardMapSchema.parse({version:EXCEL_MAP_VERSION,updatedAt,nodes,edges,layout:{drawingVersion:drawing.version,blocks,slots}});
  return {...map,referenceLayout:drawing};
}

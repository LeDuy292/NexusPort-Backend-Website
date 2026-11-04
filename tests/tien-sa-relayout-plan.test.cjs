const {test}=require('node:test');
const assert=require('node:assert/strict');
const {buildRelayoutPlan}=require('../database/scripts/tien-sa-relayout-plan.cjs');
const full=require('../node-core/src/shared/yard/tien-sa-plan.json');
const design={...full,areas:full.areas.filter(a=>['C01','D01'].includes(a.code))};
function fixture(){return {
 containers:[{id:'dry',container_no:'DRY',container_type_id:'40'},{id:'cold',container_no:'COLD',container_type_id:'20',is_reefer:true},{id:'long',container_no:'LONG',container_type_id:'45'}],
 container_types:[20,40,45].map(n=>({id:String(n),size:'ft'+n,code:String(n)})),
 bookings:[{id:'out',booking_type:'pickup',status:'approved',created_at:'2026-01-01'},{id:'next',booking_type:'dropoff',status:'pending',created_at:'2026-01-02'},{id:'in',booking_type:'dropoff',status:'checked_in',created_at:'2026-01-03'}],
 booking_containers:[{booking_id:'out',container_id:'dry',container_visit_id:'active'},{booking_id:'next',container_id:'dry',container_visit_id:'future'},{booking_id:'in',container_id:'cold',container_visit_id:'cold-visit'}],
 container_visits:[{id:'active',container_id:'dry',visit_status:'active'},{id:'cold-visit',container_id:'cold',visit_status:'active'}],
 container_positions:[{id:'p1',container_id:'dry',is_current:true,placed_at:'2025-12-01'},{id:'p2',container_id:'long',is_current:true,placed_at:'2025-12-02'}],
 reefer_power_sessions:[{id:'power',container_id:'cold',plug_id:'plug',session_status:'ACTIVE'}],reefer_plugs:[{id:'plug'}],
 };}
test('C/D reallocation keeps physical truth, visit scopes, power links and disjoint footprints',()=>{
 const result=buildRelayoutPlan(fixture(),design);
 assert.equal(result.entities.length,4);assert.equal(result.summary.physicalContainers,2);
 const dry=result.entities.filter(e=>e.containerId==='dry');assert.equal(dry.length,2);assert.equal(dry.filter(e=>e.position).length,1);assert.notEqual(dry[0].slot.id,dry[1].slot.id);
 const cold=result.entities.find(e=>e.containerId==='cold');assert.equal(cold.slot.block,'D01');assert.equal(cold.slot.status,'reserved');assert.equal(cold.slot.hasReeferPlug,true);
 assert.equal(result.plugAssignments[0].slotId,cold.slot.id);assert.equal(cold.power[0].id,'power');
 const long=result.entities.find(e=>e.size===45);assert.equal(long.slot.moduleSpan,4);assert.equal(long.slot.containerLengthFt,45);
 const occupied=new Set();for(const s of result.slots)for(let m=s.moduleStart;m<s.moduleStart+s.moduleSpan;m++){const key=[s.block,s.row,s.tier,m].join(':');assert.equal(occupied.has(key),false,key);occupied.add(key);}
 assert.deepEqual(result.slots.map(s=>s.id),buildRelayoutPlan(fixture(),design).slots.map(s=>s.id));
});
test('ambiguous placements, unsupported sizes and insufficient plugs stop before DB writes',()=>{
 const a=fixture();a.container_positions.push({...a.container_positions[0],id:'duplicate'});assert.throws(()=>buildRelayoutPlan(a,design),/Duplicate physical/);
 const b=fixture();b.container_types[0].size='ft99';assert.throws(()=>buildRelayoutPlan(b,design),/Unsupported/);
 const c=fixture();c.reefer_power_sessions=[];c.reefer_plugs=[];assert.throws(()=>buildRelayoutPlan(c,design),/Not enough existing reefer plugs/);
});

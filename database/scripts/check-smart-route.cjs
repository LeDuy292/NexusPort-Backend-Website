'use strict';
// Runs real authenticated Node routers against only the documented isolated DB.
const assert=require('node:assert/strict');
const {randomUUID}=require('node:crypto');
const fs=require('node:fs');
const path=require('node:path');
const {Client}=require('../../node-core/node_modules/pg');
const jwt=require('../../node-core/node_modules/jsonwebtoken');
const express=require('../../node-core/node_modules/express');
const target='postgresql://postgres@127.0.0.1:55438/nxp038_test';
if(process.env.DATABASE_URL!==target)throw new Error('Exact isolated DATABASE_URL is required; fixtures may never run on a shared database.');
delete process.env.SMART_ROUTE_MAP_PATH;
const secret='nxp114-isolated-integration-only';process.env.JWT_SECRET=secret;
const pool=require('../../node-core/dist/infrastructure/database/db').default;
const {createDriverRouter}=require('../../node-core/dist/modules/driver/presentation/driver.controller');
const {createDispatcherRouter}=require('../../node-core/dist/modules/dispatcher/presentation/dispatcher.controller');
const {createGateRouter}=require('../../node-core/dist/modules/gate/presentation/gate.controller');
const {yardMapSchema,shortestRoute}=require('../../node-core/dist/modules/driver/domain/smart-route');
const db=new Client({connectionString:target});
const ids=Object.fromEntries(['user','otherUser','staff','carrier','driver','otherDriver','truck','container','declaration','secondContainer','secondDeclaration','secondDropoffOp','dropoff','pickup','dropoffOp','pickupOp','staleOp'].map(key=>[key,randomUUID()]));
const bookingIds=[ids.dropoff,ids.pickup],operationIds=[ids.dropoffOp,ids.pickupOp,ids.staleOp,ids.secondDropoffOp];
const containerIds=[ids.container,ids.secondContainer];
let server,base,slot,slotBefore,secondarySlot,visit;
const token=(role='Driver',account=ids.user)=>jwt.sign({sub:account,role},secret,{expiresIn:'10m'});
async function api(route,method='GET',body,status=200,role='Driver',account=ids.user){
 const response=await fetch(base+route,{method,headers:{Authorization:'Bearer '+token(role,account),'Content-Type':'application/json'},...(body?{body:JSON.stringify(body)}:{})});
 const data=await response.json();assert.equal(response.status,status,`${method} ${route}: ${JSON.stringify(data)}`);return data.data;
}
const routePath=bookingId=>`/drivers/me/routes/${ids.container}?bookingId=${bookingId}`;
const progress=bookingId=>api(`/drivers/me/routes/${ids.container}/progress`,'POST',{bookingId,action:'arrive'});
const confirm=(bookingId,status=200)=>api(`/drivers/me/routes/${ids.container}/confirm-container`,'POST',{bookingId,condition:'OK'},status);
async function yardCompleted(operationId,containerId=ids.container){
 await db.query('UPDATE yard_tasks SET "Status"=\'Completed\',"EndTime"=now() WHERE "Id"=$1',[operationId]);
 const columns=(await db.query("SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name='yard_operation_events'")).rows;
 const primary=columns.some(column=>column.column_name==='Id')?'"Id"':'id';
 await db.query(`INSERT INTO yard_operation_events(${primary},operation_id,container_id,driver_id,operation_status,event_type,delivery_status,occurred_at,created_at)
  VALUES($1,$2,$3,$4,'Completed','YardOperationCompleted','Published',now(),now())`,[randomUUID(),operationId,containerId,ids.driver]);
}
async function operation(operationId,bookingId,location,containerId=ids.container,visitId=visit){
 await db.query(`INSERT INTO yard_tasks("Id","TaskCode","OperationType","ContainerId","ContainerNo","DriverId","ToLocation","BlockCode","Status","CreatedAt","AssignedAt",booking_id,container_visit_id)
  SELECT $1,$2,'Pickup',$3,container_no,$4,$5,'A','Assigned',now(),now(),$6,$7 FROM containers WHERE id=$3`,[operationId,'NXP114-'+operationId,containerId,ids.driver,location,bookingId,visitId]);
}
async function gateIn(bookingId,gateId){
 await db.query("INSERT INTO gate_transactions(booking_id,gate_id,driver_id,truck_id,gate_type,status,checked_at,completed_at) VALUES($1,$2,$3,$4,'gate_in','completed',now(),now())",[bookingId,gateId,ids.driver,ids.truck]);
 // Deliberately do not create gate_transaction_containers: C# approval writes this way.
}
async function condition(targetId,restricted,congestion=0){
 return api('/dispatchers/smart-routes/conditions','PUT',{targetType:'edge',targetId,restricted,congestion},200,'Dispatcher',ids.staff);
}
async function cleanup(){
 // Historical immutability remains active throughout testing; only test fixture teardown
 // disables guards in one transaction, behind the exact localhost URL guard above.
 await db.query('BEGIN');
 try{
  await db.query("SET LOCAL session_replication_role='replica'");
  await db.query('DELETE FROM smart_yard_conditions WHERE updated_by=$1',[ids.staff]);
  await db.query('DELETE FROM driver_route_progress WHERE container_id=ANY($1::uuid[])',[containerIds]);
  await db.query('DELETE FROM container_confirmation_audits WHERE booking_id=ANY($1::uuid[])',[bookingIds]);
  for(const table of ['eir_seals','eir_damage_observations','eir_media'])await db.query(`DELETE FROM ${table} WHERE eir_transaction_id IN(SELECT id FROM eir_transactions WHERE booking_id=ANY($1::uuid[]))`,[bookingIds]);
  await db.query('DELETE FROM eir_transactions WHERE booking_id=ANY($1::uuid[])',[bookingIds]);
  await db.query('DELETE FROM yard_operation_events WHERE operation_id=ANY($1::uuid[])',[operationIds]);
  await db.query('DELETE FROM yard_tasks WHERE "Id"=ANY($1::uuid[])',[operationIds]);
  await db.query('DELETE FROM gate_transaction_containers WHERE container_id=$1',[ids.container]);
  await db.query('DELETE FROM gate_transactions WHERE booking_id=ANY($1::uuid[])',[bookingIds]);
  await db.query('DELETE FROM driver_route_instructions WHERE booking_id=ANY($1::uuid[])',[bookingIds]);
  await db.query('DELETE FROM driver_booking_notifications WHERE booking_id=ANY($1::uuid[])',[bookingIds]);
  await db.query('DELETE FROM booking_dispatch_outbox WHERE booking_id=ANY($1::uuid[])',[bookingIds]);
  await db.query('DELETE FROM booking_dispatch_state WHERE booking_id=ANY($1::uuid[])',[bookingIds]);
  await db.query('DELETE FROM booking_containers WHERE booking_id=ANY($1::uuid[])',[bookingIds]);
  await db.query('DELETE FROM bookings WHERE id=ANY($1::uuid[])',[bookingIds]);
  await db.query('DELETE FROM container_positions WHERE container_id=ANY($1::uuid[])',[containerIds]);
  await db.query('DELETE FROM yard_slot_reservations WHERE container_id=ANY($1::uuid[])',[containerIds]);
  await db.query('DELETE FROM container_visit_access WHERE carrier_id=$1',[ids.carrier]);
  await db.query('DELETE FROM container_visits WHERE container_id=ANY($1::uuid[])',[containerIds]);
  await db.query('DELETE FROM container_declarations WHERE id=ANY($1::uuid[])',[[ids.declaration,ids.secondDeclaration]]);
  await db.query('DELETE FROM containers WHERE id=ANY($1::uuid[])',[containerIds]);
  await db.query('DELETE FROM trucks WHERE id=$1',[ids.truck]);
  await db.query('DELETE FROM drivers WHERE id=ANY($1::uuid[])',[[ids.driver,ids.otherDriver]]);
  await db.query('DELETE FROM carriers WHERE id=$1',[ids.carrier]);
  await db.query('DELETE FROM users WHERE id=ANY($1::uuid[])',[[ids.user,ids.otherUser,ids.staff]]);
  if(slot&&slotBefore)await db.query('UPDATE yard_slots SET status=$2::yard_slot_status WHERE id=$1',[slot.id,slotBefore]);
  if(secondarySlot)await db.query('UPDATE yard_slots SET status=$2::yard_slot_status WHERE id=$1',[secondarySlot.id,secondarySlot.status]);
  await db.query('COMMIT');
 }catch(error){await db.query('ROLLBACK');throw error;}
}
(async()=>{
 await db.connect();
 try{
  const applied=(await db.query("SELECT version FROM nexusport_schema_versions WHERE version='20261017_017'")).rows;
  assert.equal(applied.length,1,'Apply all missing migrations before Node integration.');
  const active=(await db.query('SELECT map_data FROM smart_yard_maps WHERE is_active')).rows[0];
  assert.ok(active,'An active configured road graph is required.');
  const map=yardMapSchema.parse(active.map_data);
  for(const node of map.nodes.filter(node=>node.slotId)){
   assert.ok(shortestRoute(map,'gate:GATE-01',node.id),'Each mapped slot must have an inbound handover path.');
   assert.ok(shortestRoute(map,node.id,'gate:GATE-03'),'Each handover stop must have an outbound path.');
   for(const block of map.layout?.blocks??[])assert.ok(!(node.x>block.x&&node.x<block.x+block.width&&node.y>block.y&&node.y<block.y+block.height),'Truck stop must stay outside stacks.');
  }
  // Re-running an additive migration must preserve the active topology and operational data.
  const migration=fs.readFileSync(path.join(__dirname,'../migrations/20261017_017_smart_yard_routes.sql'),'utf8');
  await db.query(migration);
  assert.deepEqual((await db.query('SELECT map_data FROM smart_yard_maps WHERE is_active')).rows[0].map_data,active.map_data);
  for(const [id,role] of [[ids.user,'Driver'],[ids.otherUser,'Driver'],[ids.staff,'Dispatcher']])await db.query("INSERT INTO users(id,username,email,password,role,created_at,updated_at) VALUES($1,$2,$3,'test-only',$4,now(),now())",[id,'route-'+id,id+'@example.invalid',role]);
  await db.query("INSERT INTO carriers(id,company_name) VALUES($1,'NXP114 isolated fixture')",[ids.carrier]);
  for(const [driver,user] of [[ids.driver,ids.user],[ids.otherDriver,ids.otherUser]])await db.query("INSERT INTO drivers(id,carrier_id,full_name,license_number,user_id) VALUES($1,$2,'NXP114 Driver',$3,$4)",[driver,ids.carrier,'FC-'+driver,user]);
  await db.query("INSERT INTO trucks(id,carrier_id,driver_id,plate_number) VALUES($1,$2,$3,$4)",[ids.truck,ids.carrier,ids.driver,'TEST-'+ids.truck.slice(0,8)]);
  await db.query("INSERT INTO containers(id,container_type_id,container_no) SELECT $1,id,$2 FROM container_types ORDER BY code LIMIT 1",[ids.container,'NXPU'+Date.now().toString().slice(-7)]);
  await db.query("INSERT INTO container_declarations(id,container_id,declared_by,carrier_id,load_status,cargo_type,gross_weight_kg) VALUES($1,$2,$3,$4,'full','general',18000)",[ids.declaration,ids.container,ids.user,ids.carrier]);
  await db.query("INSERT INTO containers(id,container_type_id,container_no) SELECT $1,id,$2 FROM container_types ORDER BY code LIMIT 1",[ids.secondContainer,'NXPU'+(Date.now()+1).toString().slice(-7)]);
  await db.query("INSERT INTO container_declarations(id,container_id,declared_by,carrier_id,load_status,cargo_type,gross_weight_kg) VALUES($1,$2,$3,$4,'full','general',12000)",[ids.secondDeclaration,ids.secondContainer,ids.user,ids.carrier]);
  await db.query(`INSERT INTO bookings(id,carrier_id,driver_id,truck_id,booking_code,booking_type,status,appointment_start,appointment_end)
   VALUES($1,$2,$3,$4,$5,'dropoff','approved',now()-interval '4 hours',now()-interval '3 hours')`,[ids.dropoff,ids.carrier,ids.driver,ids.truck,'NXP114-IN-'+ids.dropoff]);
  await db.query('INSERT INTO booking_containers(booking_id,container_id,declaration_id) VALUES($1,$2,$3)',[ids.dropoff,ids.container,ids.declaration]);
  await db.query('INSERT INTO booking_containers(booking_id,container_id,declaration_id) VALUES($1,$2,$3)',[ids.dropoff,ids.secondContainer,ids.secondDeclaration]);
  visit=(await db.query('SELECT container_visit_id FROM booking_containers WHERE booking_id=$1 AND container_id=$2',[ids.dropoff,ids.container])).rows[0].container_visit_id;
  const secondVisit=(await db.query('SELECT container_visit_id FROM booking_containers WHERE booking_id=$1 AND container_id=$2',[ids.dropoff,ids.secondContainer])).rows[0].container_visit_id;
  await db.query("UPDATE bookings SET status='checked_in' WHERE id=$1",[ids.dropoff]);
  const gates=(await db.query("SELECT id,code FROM gates WHERE code IN('GATE-01','GATE-03')")).rows;
  const inbound=gates.find(gate=>gate.code==='GATE-01').id,outbound=gates.find(gate=>gate.code==='GATE-03').id;
  slot=(await db.query("SELECT ys.id,ys.status::text,yb.code,ys.bay,ys.row_no,ys.tier FROM yard_slots ys JOIN yard_blocks yb ON yb.id=ys.block_id WHERE yb.code='A' AND ys.row_no<=3 AND ys.tier=1 ORDER BY ys.bay,ys.row_no LIMIT 1")).rows[0];slotBefore=slot.status;
  const location=`${slot.code}-${slot.bay}-${slot.row_no}-${slot.tier}`;
  await gateIn(ids.dropoff,inbound);await operation(ids.dropoffOp,ids.dropoff,location);
  const app=express();app.use(express.json());app.use('/drivers',createDriverRouter());app.use('/dispatchers',createDispatcherRouter());app.use('/gate',createGateRouter());
  app.use((error,_req,res,_next)=>res.status(error.statusCode||500).json({success:false,message:error.message}));
  server=await new Promise(resolve=>{const instance=app.listen(0,'127.0.0.1',()=>resolve(instance));});base=`http://127.0.0.1:${server.address().port}`;
  let route=await api(routePath(ids.dropoff));assert.equal(route.journey.phase,'to_yard');assert.equal(route.navigation.status,'available');assert.equal(route.target.slotId,slot.id);
  assert.equal(route.navigation.origin.gateCode,'GATE-01');assert.ok(route.navigation.map.layout.slots.length);
  await api(routePath(ids.dropoff),'GET',null,404,'Driver',ids.otherUser);
  await api(routePath(ids.dropoff),'GET',null,403,'Dispatcher',ids.staff);
  await api('/gate/complete-out','POST',{bookingId:ids.dropoff,gateId:outbound},409,'Gate Officer',ids.staff);
  route=await progress(ids.dropoff);assert.equal(route.journey.phase,'at_yard');await confirm(ids.dropoff,409);
  await yardCompleted(ids.dropoffOp);
  await db.query('INSERT INTO container_positions(container_id,slot_id) VALUES($1,$2)',[ids.container,slot.id]);await db.query("UPDATE yard_slots SET status='occupied' WHERE id=$1",[slot.id]);
  route=await confirm(ids.dropoff);assert.equal(route.journey.phase,'at_yard');assert.equal(route.journey.canConfirmContainer,false);
  await api('/gate/complete-out','POST',{bookingId:ids.dropoff,gateId:outbound},409,'Gate Officer',ids.staff);
  await operation(ids.secondDropoffOp,ids.dropoff,'A-S2',ids.secondContainer,secondVisit);
  await api(`/drivers/me/routes/${ids.secondContainer}/progress`,'POST',{bookingId:ids.dropoff,action:'arrive'});
  await yardCompleted(ids.secondDropoffOp,ids.secondContainer);
  await api(`/drivers/me/routes/${ids.secondContainer}/confirm-container`,'POST',{bookingId:ids.dropoff,condition:'OK'});
  route=await api(routePath(ids.dropoff));assert.equal(route.journey.phase,'ready_for_gate_out');assert.equal(route.navigation.destinationNode.gateCode,'GATE-03');
  assert.equal(route.navigation.origin.id,'area-a-s2','Every container on a truck must share its most recently reported position.');
  await api('/gate/complete-out','POST',{bookingId:ids.dropoff,gateId:outbound},200,'Gate Officer',ids.staff);
  assert.equal((await api(routePath(ids.dropoff))).journey.phase,'exited');
  assert.equal((await db.query('SELECT visit_status FROM container_visits WHERE id=$1',[visit])).rows[0].visit_status,'active');
  assert.equal((await db.query('SELECT count(*)::int AS n FROM container_positions WHERE container_id=$1 AND is_current',[ids.container])).rows[0].n,1);
  await db.query(`INSERT INTO bookings(id,carrier_id,driver_id,truck_id,booking_code,booking_type,status,appointment_start,appointment_end)
   VALUES($1,$2,$3,$4,$5,'pickup','approved',now()-interval '1 hour',now()+interval '1 hour')`,[ids.pickup,ids.carrier,ids.driver,ids.truck,'NXP114-OUT-'+ids.pickup]);
  await db.query('INSERT INTO booking_containers(booking_id,container_id,declaration_id,container_visit_id) VALUES($1,$2,$3,$4)',[ids.pickup,ids.container,ids.declaration,visit]);
  await db.query("UPDATE bookings SET status='checked_in' WHERE id=$1",[ids.pickup]);await gateIn(ids.pickup,inbound);await operation(ids.pickupOp,ids.pickup,location);
  // An operation attached to another booking may never override the current trip.
  await operation(ids.staleOp,ids.dropoff,'A-S2');
  route=await api(routePath(ids.pickup));assert.equal(route.operation.id,ids.pickupOp);assert.equal(route.navigation.status,'available');
  await db.query('UPDATE yard_tasks SET container_visit_id=$2 WHERE "Id"=$1',[ids.pickupOp,secondVisit]);
  assert.equal((await api(routePath(ids.pickup))).operation,null,'An explicit booking match must not override a mismatched visit.');
  await db.query('UPDATE yard_tasks SET container_visit_id=$2 WHERE "Id"=$1',[ids.pickupOp,visit]);
  await api('/dispatchers/smart-routes/conditions','PUT',{targetType:'edge',targetId:'service-entry',restricted:true},403);
  await condition('service-entry',true);route=await api(routePath(ids.pickup));assert.equal(route.navigation.rerouted,true);assert.ok(!route.navigation.segments.some(edge=>edge.id==='service-entry'));
  await condition('ring-service-ne',true);route=await api(routePath(ids.pickup));assert.equal(route.navigation.reasonCode,'NO_SAFE_ROUTE');
  await condition('ring-service-ne',false);await condition('service-entry',false,1);route=await api(routePath(ids.pickup));assert.equal(route.navigation.rerouted,true);
  await condition('service-entry',true);await db.query("UPDATE smart_yard_conditions SET valid_until=now()-interval '1 second' WHERE updated_by=$1",[ids.staff]);
  route=await api(routePath(ids.pickup));assert.equal(route.navigation.rerouted,false,'Expired congestion/restrictions must not remain active.');
  route=await progress(ids.pickup);assert.equal(route.journey.phase,'at_yard');const revision=route.navigation.revision;
  await db.query('UPDATE yard_tasks SET "ToLocation"=\'A-S2\',"UpdatedAt"=now() WHERE "Id"=$1',[ids.pickupOp]);
  route=await api(routePath(ids.pickup));assert.equal(route.journey.phase,'to_yard');assert.equal(route.target.location,'A-S2');assert.notEqual(route.navigation.revision,revision);
  await confirm(ids.pickup,409);route=await progress(ids.pickup);assert.equal(route.journey.phase,'at_yard');await confirm(ids.pickup,409);
  await yardCompleted(ids.pickupOp);route=await api(routePath(ids.pickup));assert.equal(route.journey.canConfirmContainer,true);
  route=await confirm(ids.pickup);assert.equal(route.journey.phase,'ready_for_gate_out');assert.equal(route.navigation.destinationNode.gateCode,'GATE-03');
  // A changed destination can leave a reservation away from the physical position.
  // Physical exit releases both, without leaving an orphaned reserved slot.
  secondarySlot=(await db.query("SELECT id,status::text FROM yard_slots WHERE id<>$1 AND status='empty' ORDER BY id LIMIT 1",[slot.id])).rows[0];
  assert.ok(secondarySlot);
  await db.query("INSERT INTO yard_slot_reservations(container_id,slot_id,reserved_from,reason) VALUES($1,$2,now(),'NXP114 isolated reroute fixture')",[ids.container,secondarySlot.id]);
  await db.query("UPDATE yard_slots SET status='reserved' WHERE id=$1",[secondarySlot.id]);
  await api('/gate/complete-out','POST',{bookingId:ids.pickup,gateId:outbound},403);
  await api('/gate/complete-out','POST',{bookingId:ids.pickup,gateId:inbound},422,'Gate Officer',ids.staff);
  const exit=await api('/gate/complete-out','POST',{bookingId:ids.pickup,gateId:outbound},200,'Gate Officer',ids.staff);
  assert.deepEqual(await api('/gate/complete-out','POST',{bookingId:ids.pickup,gateId:outbound},200,'Gate Officer',ids.staff),exit);
  route=await api(routePath(ids.pickup));assert.equal(route.journey.phase,'exited');
  const monitor=await api('/dispatchers/smart-routes','GET',null,200,'Dispatcher',ids.staff);assert.equal(monitor.routes.find(item=>item.bookingId===ids.pickup).journey.phase,'exited');
  assert.equal((await db.query('SELECT visit_status FROM container_visits WHERE id=$1',[visit])).rows[0].visit_status,'completed');
  assert.equal((await db.query('SELECT status FROM containers WHERE id=$1',[ids.container])).rows[0].status,'gate_out');
  assert.equal((await db.query('SELECT count(*)::int AS n FROM eir_transactions WHERE container_visit_id=$1',[visit])).rows[0].n,2);
  assert.equal((await db.query('SELECT count(*)::int AS n FROM container_positions WHERE container_id=$1 AND is_current',[ids.container])).rows[0].n,0);
  assert.equal((await db.query('SELECT status::text FROM yard_slots WHERE id=$1',[secondarySlot.id])).rows[0].status,'empty');
  assert.equal((await db.query('SELECT count(*)::int AS n FROM yard_slot_reservations WHERE container_id=$1 AND reserved_to IS NULL',[ids.container])).rows[0].n,0);
  console.log('PASS: additive migration replay; all real slot handover paths outside stacks; authenticated linked driver; exact booking/operation and tenant isolation; Gate In without legacy link rows; arrival + yard completion + container confirmation; Dropoff exit retains stay; Pickup exit closes stay and issues two EIRs; real dispatcher restriction/congestion/expiry, no-route notification, destination refresh; gate role, exit validation/idempotency, dispatcher vehicle state.');
 }finally{
  if(server)await new Promise((resolve,reject)=>server.close(error=>error?reject(error):resolve()));
  try{await cleanup();}finally{await db.end();await pool.end();}
 }
})().catch(error=>{console.error(error.stack||error.message);process.exitCode=1;});

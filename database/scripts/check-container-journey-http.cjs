'use strict';
// Requires isolated Node :5189, C# :5188 and the dedicated PostgreSQL test DB.
const assert = require('node:assert/strict');
const { randomUUID } = require('node:crypto');
const jwt = require('../../node-core/node_modules/jsonwebtoken');
const { Client } = require('../../node-core/node_modules/pg');
if (process.env.DATABASE_URL !== 'postgresql://postgres@127.0.0.1:55438/nxp038_test') throw new Error('Isolated test DB required');
const db = new Client({ connectionString: process.env.DATABASE_URL });
const users = [randomUUID(), randomUUID()], carriers = [randomUUID(), randomUUID()];
let containerId;
const fixtureGate = randomUUID();
const trucks=[randomUUID(),randomUUID()],drivers=[randomUUID(),randomUUID()];
const token = (index, role) => jwt.sign({ sub: users[index], role: role || 'Transport Company' }, 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!', { expiresIn: '5m' });
async function api(port,path,index,method='GET',body,status=200,role) {
  const response = await fetch(`http://127.0.0.1:${port}${path}`, { method, headers: { Authorization: `Bearer ${token(index,role)}`, 'Content-Type':'application/json' }, ...(body ? { body:JSON.stringify(body) } : {}) });
  const data = await response.json().catch(()=>null);
  assert.equal(response.status,status,`${method} ${path}: ${JSON.stringify(data)}`);
  return port===5189 ? data.data : data;
}
(async()=>{
  await db.connect();
  try {
    for(let i=0;i<2;i++) {
      await db.query("INSERT INTO users(id,username,email,password,role,is_active,created_at,updated_at) VALUES($1,$2,$3,'test-only','Transport Company',true,now(),now())",[users[i],'http-'+users[i],users[i]+'@example.invalid']);
      await db.query('INSERT INTO carriers(id,company_name) VALUES($1,$2)',[carriers[i],'HTTP fixture '+i]);
      await db.query('INSERT INTO carrier_users(carrier_id,user_id) VALUES($1,$2)',[carriers[i],users[i]]);
      await db.query("INSERT INTO drivers(id,carrier_id,full_name,license_number) VALUES($1,$2,$3,$4)",[drivers[i],carriers[i],'HTTP driver '+i,'FC-'+drivers[i]]);
      await db.query("INSERT INTO trucks(id,carrier_id,plate_number,vehicle_type,driver_id) VALUES($1,$2,$3,'Truck 24T',$4)",[trucks[i],carriers[i],'HTTP-'+trucks[i].slice(0,8),drivers[i]]);
    }
    const created=await api(5189,'/api/v1/containers/intake/transport/manual',0,'POST',{containerNumber:'CSQU3054383',containerTypeCode:'40RF',cargoType:'reefer',requiredPowerKw:10,customerName:'PRIVATE HTTP A',sealNumber:'ACTUAL-SEAL',grossWeightKg:18000},201);
    containerId=created.containerId;
    assert.ok(created.declarationId); assert.equal(created.visitId,undefined);
    const identity=await api(5189,'/api/v1/containers/lookup?containerNumber=CSQU3054383',1);
    assert.equal(identity.customerName,undefined);assert.equal(identity.sealNumber,undefined);
    await api(5189,`/api/v1/containers/${containerId}`,1,'GET',null,403);
    await api(5189,'/api/v1/containers/journeys',1,'GET',null,422);
    const payload={carrierId:carriers[1],vehiclePlate:'TEST-PLATE-A',bookingCode:'HTTP-'+randomUUID(),bookingType:'Dropoff',appointmentStart:'2032-01-01T08:00Z',appointmentEnd:'2032-01-01T09:00Z',containerSelections:[{containerId,declarationId:created.declarationId}]};
    const recA=await api(5188,'/api/v1/Booking/recommend-selection',0,'POST',{containerId,declarationId:created.declarationId,bookingType:'Dropoff'});
    assert.equal(recA.recommendedTruckId,trucks[0]); assert.equal(recA.recommendedDriverId,drivers[0]);assert.equal(recA.containerGrossWeightTon,18);assert.equal(recA.payloadRatio,75);
    const booking=await api(5188,'/api/v1/Booking',0,'POST',payload,201);
    assert.equal(booking.carrierId,carriers[0]);
    const visit=booking.containerSelections[0].containerVisitId;
    assert.ok(visit,'C# must return the visit assigned by the DB trigger');
    const list=await api(5188,'/api/v1/Booking',1); assert.equal(list.totalCount,0);
    await api(5188,`/api/v1/Booking/${booking.id}`,1,'GET',null,401);
    await api(5188,'/api/v1/Booking',1,'POST',{...payload,bookingCode:'HTTP-'+randomUUID()},422);
    const yardCode=booking.containerSelections[0].yardCode;
    assert.match(yardCode,/^TC-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}$/);
    const recB=await api(5188,'/api/v1/Booking/recommend-selection',1,'POST',{containerId,yardCode,bookingType:'Pickup'});
    assert.equal(recB.recommendedTruckId,trucks[1]);assert.equal(recB.recommendedDriverId,drivers[1]);assert.equal(recB.containerGrossWeightTon,18);
    const evaluation=await api(5188,'/api/v1/Booking/evaluate-payload?truckId='+trucks[1]+'&customGrossWeightTon=18',1);
    assert.equal(evaluation.payloadRatio,75);
    const pickupPayload={...payload,bookingCode:'HTTP-'+randomUUID(),bookingType:'Pickup',appointmentStart:'2032-01-01T15:00Z',appointmentEnd:'2032-01-01T16:00Z',containerSelections:[{containerId,yardCode:yardCode.replace(/^TC-/,'YARD-')}]};
    const beforeDeclarationCount=Number((await db.query('SELECT count(*) FROM container_declarations')).rows[0].count);
    await api(5188,'/api/v1/Booking',1,'POST',{...pickupPayload,containerSelections:[{containerId,yardCode:'TC-0000-0000-0000'}]},422);
    await api(5188,'/api/v1/Booking',1,'POST',{...pickupPayload,containerSelections:[{containerId,declarationId:created.declarationId,containerVisitId:visit}]},422);
    assert.equal(Number((await db.query('SELECT count(*) FROM container_declarations')).rows[0].count),beforeDeclarationCount);
    assert.deepEqual(await api(5189,`/api/v1/containers/own-visits/${containerId}`,1),[]);
    // Failed scheduling must not grant access or leave a copied declaration.
    await api(5188,'/api/v1/Booking',1,'POST',{...pickupPayload,appointmentStart:'2031-12-31T08:00Z',appointmentEnd:'2031-12-31T09:00Z'},422);
    assert.deepEqual(await api(5189,`/api/v1/containers/own-visits/${containerId}`,1),[]);
    assert.equal(Number((await db.query('SELECT count(*) FROM container_declarations')).rows[0].count),beforeDeclarationCount);
    const outbound=await api(5188,'/api/v1/Booking',1,'POST',pickupPayload,201);
    assert.equal(outbound.containerSelections[0].containerVisitId,visit);
    const copied=(await db.query('SELECT carrier_id,customer_name,seal_number,gross_weight_kg FROM container_declarations WHERE id=$1',[outbound.containerSelections[0].declarationId])).rows[0];
    assert.equal(copied.carrier_id,carriers[1]); assert.equal(copied.customer_name,'PRIVATE HTTP A'); assert.equal(copied.seal_number,'ACTUAL-SEAL');
    assert.equal(Number(copied.gross_weight_kg),18000);
    await api(5188,'/api/v1/Booking',0,'POST',{...pickupPayload,bookingCode:'HTTP-'+randomUUID()},422);
    assert.equal(Number((await db.query('SELECT count(*) FROM container_declarations')).rows[0].count),beforeDeclarationCount+1);
    const bookingAgain=await api(5188,`/api/v1/Booking/${booking.id}`,0);
    assert.equal(bookingAgain.containerSelections[0].yardCode,yardCode);
    const publicSearch=await api(5189,'/api/v1/containers/journeys?containerNumber=CSQU',1);
    assert.equal(publicSearch.items[0].yardCode,undefined);assert.equal(publicSearch.items[0].pickupCode,undefined);
    // Recommendation endpoints reject another company's declaration / wrong code.
    await api(5188,'/api/v1/Booking/recommend-selection',1,'POST',{containerId,declarationId:created.declarationId,bookingType:'Dropoff'},422);
    await api(5188,'/api/v1/Booking/recommend-selection',1,'POST',{containerId,yardCode:'TC-0000-0000-0000',bookingType:'Pickup'},422);
    // Regression: approving an 08:00Z Dropoff must not shift it to 15:00Z
    // and collide with its valid paired 15:00Z Pickup on a UTC+7 machine.
    const beforeApproval=(await db.query('SELECT appointment_start,appointment_end,valid_from,valid_to FROM bookings WHERE id=$1',[booking.id])).rows[0];
    await api(5188,`/api/v1/Booking/${booking.id}/approve`,0,'POST',{},200,'Dispatcher');
    const afterApproval=(await db.query('SELECT appointment_start,appointment_end,valid_from,valid_to FROM bookings WHERE id=$1',[booking.id])).rows[0];
    assert.deepEqual(afterApproval,beforeApproval,'Approval must not rewrite any appointment/validity time');
    const history=await api(5189,'/api/v1/containers/journeys?containerNumber=CSQU&plateNumber=TESTPLATEA',0,'GET',null,200,'Dispatcher');
    assert.equal(history.items.length,1); assert.equal(history.items[0].bookings.length,2);
    assert.equal(history.items[0].customerName,'PRIVATE HTTP A');
    await api(5188,'/api/v1/Gate/complete-out',0,'POST',{bookingId:outbound.id,gateId:randomUUID()},403);
    await api(5188,'/api/v1/Gate/complete-out',0,'POST',{bookingId:outbound.id,gateId:randomUUID()},422,'Gate Officer');

    await api(5188,`/api/v1/Booking/${outbound.id}`,1,'PUT',{appointmentStart:'2032-01-01T08:00Z',appointmentEnd:'2032-01-01T09:00Z',containerIds:[containerId]},422);
    const own=await api(5189,`/api/v1/containers/own-visits/${containerId}`,0);assert.equal(own.length,1);
    assert.equal((await api(5189,`/api/v1/containers/own-visits/${containerId}`,1)).length,1);
    const shared=await api(5188,'/api/v1/Container?containerNumber=CSQU',1);assert.equal(shared[0].sealNumber,undefined);
    assert.equal((await db.query('SELECT status FROM containers WHERE id=$1',[containerId])).rows[0].status,'expected');
    await db.query("UPDATE bookings SET status='approved' WHERE id=$1",[booking.id]);
    await db.query("INSERT INTO gates(id,code,name) VALUES($1,'TEST-IN','HTTP test gate')",[fixtureGate]);
    await api(5188,'/api/v1/Gate/approve-in',0,'POST',{bookingNumber:booking.bookingCode,containerNumber:'CSQU3054383',gateCode:'TEST-IN'},422,'Gate Officer');
    await db.query("UPDATE bookings SET appointment_start=now()-interval '1 minute',appointment_end=now()+interval '1 minute',valid_from=now()-interval '1 minute',valid_to=now()+interval '1 minute' WHERE id=$1",[booking.id]);
    await api(5188,'/api/v1/Gate/approve-in',0,'POST',{bookingNumber:booking.bookingCode,containerNumber:'CSQU3054383',gateCode:'TEST-IN'},200,'Gate Officer');
    assert.equal((await db.query('SELECT status FROM containers WHERE id=$1',[containerId])).rows[0].status,'gate_in');
    await db.query("UPDATE bookings SET status='completed' WHERE id=$1",[outbound.id]);
    await api(5188,'/api/v1/Gate/complete-out',0,'POST',{bookingId:outbound.id,gateId:fixtureGate},200,'Gate Officer');
    assert.equal((await db.query('SELECT status FROM containers WHERE id=$1',[containerId])).rows[0].status,'gate_out');
    await api(5188,'/api/v1/Booking',1,'POST',{...pickupPayload,bookingCode:'HTTP-'+randomUUID(),appointmentStart:'2032-01-03T08:00Z',appointmentEnd:'2032-01-03T09:00Z'},422);
    assert.equal((await db.query('SELECT count(*)::int AS n FROM eir_transactions WHERE container_visit_id=$1',[visit])).rows[0].n,2);

    console.log('PASS: approval preserves schedule instants on UTC+7 with a valid paired Pickup; original fleet AI and payload analysis use exact private cargo and own-company fleet; transferable short yard code, cross-company Pickup without redeclaration, invalid/replayed codes and transactional rollback; gate-in → completed Pickup → gate-out, two immutable receipts; HTTP declaration → Dropoff → linked Pickup, tenant isolation, public identity and reschedule validation');
  } catch(e) { console.error('HTTP assertion:',e.message); throw e; } finally {
    // Test-only teardown: the exact URL guard above excludes any shared DB.
    // Immutable historical fixtures require temporarily disabling their guards,
    // inside one transaction, for these fixture IDs only.
    await db.query('BEGIN');
    const guards=[['booking_containers','trg_validate_changed_link'],['container_visits','trg_protect_visit'],['eir_transactions','trg_protect_receipt'],['eir_seals','trg_protect_receipt_child'],['eir_damage_observations','trg_protect_receipt_child'],['eir_media','trg_protect_receipt_child']];
    try {
    for(const [table,trigger] of guards)await db.query(`ALTER TABLE ${table} DISABLE TRIGGER ${trigger}`);
    await db.query('DELETE FROM eir_transactions WHERE container_visit_id IN (SELECT id FROM container_visits WHERE declared_by=ANY($1::uuid[]))',[users]);
    await db.query('DELETE FROM gate_transactions WHERE booking_id IN (SELECT id FROM bookings WHERE carrier_id=ANY($1::uuid[]))',[carriers]);
    if((await db.query("SELECT to_regclass('public.gate_verification_records') AS t")).rows[0].t) await db.query('DELETE FROM gate_verification_records WHERE booking_id IN (SELECT id FROM bookings WHERE carrier_id=ANY($1::uuid[]))',[carriers]);
    await db.query('DELETE FROM booking_containers WHERE booking_id IN (SELECT id FROM bookings WHERE carrier_id=ANY($1::uuid[]))',[carriers]);
    await db.query('DELETE FROM bookings WHERE carrier_id=ANY($1::uuid[])',[carriers]);
    await db.query('DELETE FROM container_visit_access WHERE carrier_id=ANY($1::uuid[])',[carriers]);
    await db.query('DELETE FROM container_visits WHERE declared_by=ANY($1::uuid[])',[users]);
    await db.query('DELETE FROM container_declarations WHERE declared_by=ANY($1::uuid[]) OR carrier_id=ANY($2::uuid[])',[users,carriers]);
    if(containerId)await db.query('DELETE FROM containers WHERE id=$1',[containerId]);
    await db.query('DELETE FROM notifications WHERE receiver_user_id=ANY($1::uuid[])',[users]);
    await db.query('DELETE FROM carrier_users WHERE user_id=ANY($1::uuid[])',[users]);
    await db.query('DELETE FROM trucks WHERE id=ANY($1::uuid[])',[trucks]);
    await db.query('DELETE FROM drivers WHERE id=ANY($1::uuid[])',[drivers]);
    await db.query('DELETE FROM carriers WHERE id=ANY($1::uuid[])',[carriers]);
    await db.query('DELETE FROM users WHERE id=ANY($1::uuid[])',[users]);
    await db.query('DELETE FROM gates WHERE id=$1',[fixtureGate]);
    for(const [table,trigger] of guards)await db.query(`ALTER TABLE ${table} ENABLE TRIGGER ${trigger}`);
    await db.query('COMMIT');
    }catch(e){await db.query('ROLLBACK');throw e;}finally{await db.end();}
  }
})().catch(e=>{console.error(e.message);process.exitCode=1;});

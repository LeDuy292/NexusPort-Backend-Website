'use strict';
// Opt-in fixtures are allowed only in this dedicated localhost database.
if (process.env.DISPATCH_TEST_DB !== 'postgresql://postgres@127.0.0.1:54817/nxp_driver_dispatch_test') {
  throw new Error('Set DISPATCH_TEST_DB to the exact dedicated localhost test database.');
}
Object.assign(process.env, { DB_TARGET:'local', DB_HOST:'127.0.0.1', DB_PORT:'54817',
  DB_NAME:'nxp_driver_dispatch_test', DB_USER:'postgres', DB_PASSWORD:'test', DB_SSL_MODE:'disable', NODE_ENV:'test' });
const fs=require('fs');
const path=require('path');
const assert=require('node:assert/strict');
const {Client}=require('pg');
const http=require('http');
const {io:socketClient}=require('socket.io-client');
const id=n=>'00000000-0000-4000-8000-'+String(n).padStart(12,'0');
const driverId=id(1), otherId=id(2), dispatcherId=id(3), profileId=id(5), bookingId=id(10);
let sequelize,server,socketServer,stopWorker;
const sockets=[];
async function main(){
  const admin=new Client({host:'127.0.0.1',port:54817,user:'postgres',database:'postgres'});
  await admin.connect();
  if(!(await admin.query("SELECT 1 FROM pg_database WHERE datname='nxp_driver_dispatch_test'")).rowCount) {
    await admin.query('CREATE DATABASE nxp_driver_dispatch_test');
  }
  await admin.end();
  ({sequelize}=require('../../src/config/database'));sequelize.options.logging=false;
  const sql=String.raw`
    DROP SCHEMA public CASCADE; CREATE SCHEMA public;
    CREATE TABLE users(id uuid PRIMARY KEY,username text,email text,password text,role text,full_name text,
      is_active boolean DEFAULT true,reset_token text,reset_token_exp timestamptz,created_at timestamptz DEFAULT now(),updated_at timestamptz DEFAULT now());
    CREATE TABLE carriers(id uuid PRIMARY KEY,company_name text);
    CREATE TABLE drivers(id uuid PRIMARY KEY,user_id uuid REFERENCES users(id),carrier_id uuid REFERENCES carriers(id),full_name text,license_number text);
    CREATE TABLE trucks(id uuid PRIMARY KEY,plate_number text);
    CREATE TABLE bookings(id uuid PRIMARY KEY,booking_code text,booking_type text,status text,driver_id uuid REFERENCES drivers(id),carrier_id uuid,
      truck_id uuid REFERENCES trucks(id),vehicle_plate text,appointment_start timestamptz,appointment_end timestamptz,approved_at timestamptz,
      rejected_reason text,description text,created_at timestamptz DEFAULT now(),gate_type text);
    CREATE TABLE container_types(id uuid PRIMARY KEY,code text);
    CREATE TABLE containers(id uuid PRIMARY KEY,container_no text,container_type_id uuid);
    CREATE TABLE container_declarations(id uuid PRIMARY KEY,carrier_id uuid,cargo_type text,gross_weight_kg numeric,load_status text,seal_number text,customer_name text);
    CREATE TABLE container_cargo_types(code text,name_vi text);
    CREATE TABLE booking_containers(booking_id uuid,container_id uuid,declaration_id uuid);
    CREATE TABLE driver_booking_notifications(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),user_id uuid REFERENCES users(id),booking_id uuid REFERENCES bookings(id),
      event_key text,title text,message text,is_read boolean DEFAULT false,created_at timestamptz DEFAULT now(),UNIQUE(user_id,booking_id,event_key));
    CREATE TABLE nexusport_schema_versions(version text PRIMARY KEY,description text);
  `;
  await sequelize.query(sql);
  await sequelize.query(fs.readFileSync(path.join(__dirname,'../../database/migrations/20261017_015_booking_dispatch_arrivals.sql'),'utf8'));
  await sequelize.query(fs.readFileSync(path.join(__dirname,'../../database/migrations/20261017_016_dispatch_vehicle_reassignment.sql'),'utf8'));
  await sequelize.query(`INSERT INTO users(id,username,email,password,role,full_name) VALUES
    (:driverId,'test_driver','driver@test.local','test','Driver','Tài xế demo'),
    (:otherId,'test_other','other@test.local','test','Driver','Tài xế khác'),
    (:dispatcherId,'test_dispatcher','dispatch@test.local','test','Dispatcher','Điều độ demo');
    INSERT INTO carriers VALUES(:carrierId,'Công ty vận tải demo');
    INSERT INTO drivers VALUES(:profileId,:driverId,:carrierId,'Tài xế demo','TEST-LICENSE');
    INSERT INTO trucks VALUES(:truckId,'43C-123.45');
    INSERT INTO bookings(id,booking_code,booking_type,status,driver_id,carrier_id,truck_id,appointment_start,appointment_end,approved_at)
    VALUES(:bookingId,'BK-DEMO-001','Dropoff','approved',:profileId,:carrierId,:truckId,now()-interval '1 minute',now()+interval '30 minutes',now()),
      (:earlyId,'BK-DEMO-EARLY','Pickup','approved',:profileId,:carrierId,:truckId,now()+interval '3 hours',now()+interval '4 hours',now());
    INSERT INTO container_types VALUES(:typeId,'40HC');
    INSERT INTO containers VALUES(:containerId,'DEMO1234567',:typeId);
    INSERT INTO container_declarations VALUES(:declarationId,:carrierId,'general',18000,'laden','SEAL-DEMO','Khách hàng demo');
    INSERT INTO booking_containers VALUES(:bookingId,:containerId,:declarationId);`,
    {replacements:{driverId,otherId,dispatcherId,profileId,bookingId,carrierId:id(4),truckId:id(7),earlyId:id(11),typeId:id(20),containerId:id(21),declarationId:id(22)}});
  const {generateToken}=require('../../src/utils/jwt');
  const tokens={driver:generateToken({id:driverId,role:'Driver'}),other:generateToken({id:otherId,role:'Driver'}),dispatcher:generateToken({id:dispatcherId,role:'Dispatcher'})};
  const app=require('../../src/app');
  const {initSocket}=require('../../src/config/socket');
  const {pumpDispatchEvents,startDispatchRealtime}=require('../../src/modules/booking-dispatch/dispatch.realtime');
  server=http.createServer(app);socketServer=initSocket(server);
  await new Promise(resolve=>server.listen(5191,'127.0.0.1',resolve));
  const base='http://127.0.0.1:5191';
  async function api(role,url,body){
    const response=await fetch(base+url,{method:body?'POST':'GET',headers:{Authorization:'Bearer '+tokens[role],'Content-Type':'application/json'},...(body?{body:JSON.stringify(body)}:{})});
    return {status:response.status,body:await response.json()};
  }
  const call=(role,action,version,extra={})=>api(role,'/api/booking-dispatch/'+bookingId+'/'+action,{expectedVersion:version,...extra});
  async function socket(role){
    const s=socketClient(base,{auth:{token:tokens[role]},transports:['websocket'],reconnection:false});sockets.push(s);
    await new Promise((resolve,reject)=>{s.once('connect',resolve);s.once('connect_error',reject)});return s;
  }
  const [driverSocket,dispatcherSocket,otherSocket]=await Promise.all([socket('driver'),socket('dispatcher'),socket('other')]);
  const otherEvents=[];otherSocket.on('booking:dispatch_updated',e=>otherEvents.push(e));
  const receive=s=>new Promise((resolve,reject)=>{const t=setTimeout(()=>reject(new Error('No realtime event')),4000);s.once('booking:dispatch_updated',e=>{clearTimeout(t);resolve(e)})});
  assert.equal((await call('other','arrive',0)).status,404);
  assert.equal((await call('driver','call',0)).status,403);
  assert.equal((await api('driver','/api/booking-dispatch/'+id(11)+'/arrive',{expectedVersion:0})).status,409);
  const received=Promise.all([receive(driverSocket),receive(dispatcherSocket)]);
  assert.equal((await call('driver','arrive',0)).status,200);
  await pumpDispatchEvents();
  const emitted=await received;assert.equal(emitted[0].bookingId,bookingId);assert.equal(emitted[1].version,1);
  const race=await Promise.all([call('dispatcher','call',1),call('dispatcher','call',1)]);
  assert.deepEqual(race.map(r=>r.status).sort(),[200,409]);
  const [notes]=await sequelize.query("SELECT * FROM driver_booking_notifications WHERE event_key='dispatch:2'");
  assert.equal(notes.length,1);assert.equal(notes[0].user_id,driverId);
  assert.equal((await call('driver','acknowledge',2)).body.data.booking.dispatch.status,'acknowledged');
  assert.equal((await call('dispatcher','delay',3,{minutes:15,note:'Thiết bị đang phục vụ xe trước.'})).body.data.booking.coordination.isHeld,true);
  assert.equal((await call('dispatcher','call',4)).status,409);
  assert.equal((await call('dispatcher','release',4)).status,200);
  assert.equal((await call('dispatcher','call',5)).status,200);
  await sequelize.query("UPDATE booking_dispatch_state SET call_expires_at=now()-interval '1 second' WHERE booking_id=:id",{replacements:{id:bookingId}});
  assert.equal((await call('driver','acknowledge',6)).status,409);
  await pumpDispatchEvents();
  const before=(await api('driver','/api/driver-bookings/'+bookingId)).body.data.booking;
  assert.equal(before.dispatch.action,'call_expired');assert.equal(before.dispatch.version,7);
  const [countBefore]=await sequelize.query('SELECT count(*)::int AS count FROM booking_dispatch_outbox');
  await assert.rejects(sequelize.transaction(async transaction=>{
    await sequelize.query("UPDATE booking_dispatch_state SET version=version+1,status='called',action='called' WHERE booking_id=:id",{replacements:{id:bookingId},transaction});
    throw new Error('rollback fixture');
  }),/rollback fixture/);
  const [countAfter]=await sequelize.query('SELECT count(*)::int AS count FROM booking_dispatch_outbox');
  assert.equal(countAfter[0].count,countBefore[0].count);
  await sequelize.query("UPDATE bookings SET appointment_end=appointment_end+interval '1 minute' WHERE id=:id",{replacements:{id:bookingId}});
  const invalidated=(await api('driver','/api/driver-bookings/'+bookingId)).body.data.booking;
  assert.equal(invalidated.dispatch.status,'withdrawn');assert.equal(invalidated.status,'approved');
  assert.equal((await call('driver','arrive',invalidated.dispatch.version)).status,200);
  await sequelize.query("UPDATE bookings SET vehicle_plate='43C-999.99' WHERE id=:id",{replacements:{id:bookingId}});
  const vehicleChanged=(await api('driver','/api/driver-bookings/'+bookingId)).body.data.booking;
  assert.equal(vehicleChanged.dispatch.status,'withdrawn');
  const localDate=new Intl.DateTimeFormat('sv-SE',{timeZone:'Asia/Ho_Chi_Minh'}).format(new Date());
  const midnight=new Date(localDate+'T00:00:00+07:00').getTime();
  await sequelize.query("INSERT INTO bookings(id,booking_code,booking_type,status,driver_id,carrier_id,truck_id,appointment_start,appointment_end,approved_at) SELECT :overnightId,'BK-OVERNIGHT','Dropoff','approved',driver_id,carrier_id,truck_id,:start,:end,now() FROM bookings WHERE id=:id",
    {replacements:{overnightId:id(99),id:bookingId,start:new Date(midnight-1800000),end:new Date(midnight+1800000)}});
  const board=(await api('dispatcher','/api/booking-dispatch/board?date='+localDate)).body.data;
  assert.ok(board.bookings.some(b=>b.id===id(99)),'Schedule must include appointments starting before midnight but ending in the selected day.');
  await sequelize.query('DELETE FROM bookings WHERE id=:id',{replacements:{id:id(99)}});
  assert.equal(otherEvents.length,0);
  console.log('PASS integration: real SQL, driver ownership, socket routing by login, concurrency, durable notifications, delay, acknowledgment, expiry, rollback, C#-style reschedule and vehicle change invalidation; booking stays approved.');
  // Leave a fresh fixture for the optional browser run.
  await sequelize.query("DELETE FROM booking_dispatch_state; DELETE FROM booking_dispatch_outbox; DELETE FROM driver_booking_notifications;");
  if(process.argv.includes('--serve')){
    stopWorker=startDispatchRealtime();
    const session=(key,role,username)=>({id:key,username,email:username+'@test.local',fullName:username,role,isActive:true,token:tokens[role==='Driver'?'driver':'dispatcher']});
    fs.writeFileSync(path.join(__dirname,'../../../outputs/driver-dispatch-test/browser-sessions.json'),JSON.stringify({
      driver:session(driverId,'Driver','test_driver'),dispatcher:session(dispatcherId,'Dispatcher','test_dispatcher'),bookingId
    }));
    console.log('Fixture HTTP/socket server: 127.0.0.1:5191');
    server.on('close',()=>{stopWorker?.();});
    return;
  }
  await close();
}
async function close(){
  stopWorker?.();sockets.forEach(s=>s.disconnect());
  if(socketServer)await new Promise(resolve=>socketServer.close(resolve));
  else if(server)await new Promise(resolve=>server.close(resolve));
  if(sequelize)await sequelize.close();
}
process.on('SIGINT',()=>void close().then(()=>process.exit(0)));
process.on('SIGTERM',()=>void close().then(()=>process.exit(0)));
main().catch(async e=>{console.error(e);await close();process.exitCode=1;});

'use strict';
// Dedicated, local throwaway PostgreSQL only. Never reads DATABASE_URL/.env.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {Client}=require('../../node-core/node_modules/pg');
const connection={host:'127.0.0.1',port:54818,user:'gate_flow_test',database:'postgres'};
const id=n=>`11111111-1111-4111-8111-${String(n).padStart(12,'0')}`;
async function main(){
  const client=new Client(connection);
  await client.connect();
  try{
    const server=(await client.query('SELECT host(inet_server_addr()) AS address,inet_server_port() AS port,current_user AS username')).rows[0];
    assert.equal(server.address,'127.0.0.1');assert.equal(server.port,54818);assert.equal(server.username,'gate_flow_test');
    await client.query(`DROP SCHEMA IF EXISTS gate_entry_fixture CASCADE; CREATE SCHEMA gate_entry_fixture; SET search_path TO gate_entry_fixture;
      CREATE TABLE nexusport_schema_versions(version text PRIMARY KEY,description text);
      CREATE TABLE users(id uuid PRIMARY KEY);
      CREATE TABLE drivers(id uuid PRIMARY KEY,user_id uuid,full_name text);
      CREATE TABLE trucks(id uuid PRIMARY KEY,plate_number text);
      CREATE TABLE bookings(id uuid PRIMARY KEY,driver_id uuid,truck_id uuid,vehicle_plate text,booking_code text,status text,
        appointment_start timestamptz,appointment_end timestamptz);
      CREATE TABLE gates(id uuid PRIMARY KEY);
      CREATE TABLE gate_transactions(id uuid PRIMARY KEY,booking_id uuid);
      CREATE TABLE driver_booking_notifications(user_id uuid,booking_id uuid,event_key text,title text,message text,UNIQUE(user_id,booking_id,event_key));`);
    for(const file of ['20261017_015_booking_dispatch_arrivals.sql','20261017_016_dispatch_vehicle_reassignment.sql','20261018_018_dispatch_gate_entry_orders.sql','20261018_019_gate_verification_records.sql'])
      await client.query(fs.readFileSync(path.join(__dirname,'../../database/migrations',file),'utf8'));
    await client.query('INSERT INTO users VALUES($1),($2);',[id(1),id(2)]);
    await client.query("INSERT INTO drivers VALUES($1,$2,'Test driver');",[id(3),id(1)]);
    await client.query("INSERT INTO trucks VALUES($1,'43C-123.45');",[id(4)]);
    await client.query("INSERT INTO bookings VALUES($1,$2,$3,'43C-123.45','GATE-TEST','approved',now()+interval '6 hours',now()+interval '7 hours')",[id(5),id(3),id(4)]);
    const current=async()=>(await client.query('SELECT * FROM gate_entry_orders ORDER BY issued_at DESC,id DESC')).rows;
    const call=async(version)=>client.query(`INSERT INTO booking_dispatch_state(booking_id,driver_id,status,action,version,called_at,call_expires_at,updated_by)
      VALUES($1,$2,'called','called',$3,clock_timestamp(),now()+interval '10 minutes',$4)
      ON CONFLICT(booking_id) DO UPDATE SET status='called',action='called',version=$3,called_at=clock_timestamp(),call_expires_at=now()+interval '10 minutes',updated_by=$4`,[id(5),id(3),version,id(2)]);
    await call(1);
    let order=(await current())[0];
    assert.equal(order.status,'pending_ack');assert.equal(order.vehicle_plate,'43C12345');assert.equal(order.issued_by,id(2));
    assert.equal((await client.query('SELECT count(*)::int AS n FROM gate_transactions')).rows[0].n,0);
    await client.query("UPDATE booking_dispatch_state SET status='acknowledged',action='acknowledged',acknowledged_at=clock_timestamp(),version=2 WHERE booking_id=$1",[id(5)]);
    order=(await current())[0];assert.equal(order.status,'active');assert.equal(order.valid_until-order.valid_from,15*60000);
    await client.query("UPDATE bookings SET vehicle_plate='43C-999.99' WHERE id=$1",[id(5)]);
    assert.equal((await current())[0].status,'revoked');
    await call(4);
    await client.query("UPDATE booking_dispatch_state SET status='acknowledged',action='acknowledged',acknowledged_at=now()-interval '16 minutes',version=5 WHERE booking_id=$1",[id(5)]);
    const expire=(await client.query(`UPDATE booking_dispatch_state SET status='ready',action='entry_expired',version=6,message='Entry expired'
      WHERE booking_id=$1 AND EXISTS(SELECT 1 FROM gate_entry_orders e WHERE e.booking_id=$1 AND e.status='active' AND e.valid_until<=now()) RETURNING booking_id`,[id(5)])).rows;
    assert.equal(expire.length,1);assert.equal((await current()).filter(x=>x.status==='expired').length,1);
    assert.equal((await client.query("SELECT count(*)::int AS n FROM driver_booking_notifications WHERE event_key='dispatch:6'")).rows[0].n,1);
    await call(7);
    await client.query("UPDATE booking_dispatch_state SET status='acknowledged',action='acknowledged',acknowledged_at=clock_timestamp(),version=8 WHERE booking_id=$1",[id(5)]);
    order=(await current()).find(x=>x.status==='active');assert.ok(order);
    await client.query('INSERT INTO gates VALUES($1)',[id(6)]);
    await client.query(`INSERT INTO gate_barrier_commands(id,gate_id,gate_code,action,source,entry_order_id,requested_by)
      VALUES($1,$2,'GATE-01','open','automatic',$3,$4)`,[id(7),id(6),order.id,id(2)]);
    assert.equal((await client.query('SELECT status FROM gate_barrier_commands')).rows[0].status,'simulated');
    assert.equal((await client.query('SELECT count(*)::int AS n FROM gate_transactions')).rows[0].n,0,'opening is not passage');
    await client.query('BEGIN');
    await client.query('INSERT INTO gate_transactions VALUES($1,$2)',[id(8),id(5)]);
    await client.query("UPDATE gate_entry_orders SET status='consumed',consumed_at=now(),gate_transaction_id=$1 WHERE id=$2",[id(8),order.id]);
    await client.query("UPDATE bookings SET status='checked_in' WHERE id=$1",[id(5)]);
    await client.query('COMMIT');
    assert.equal((await client.query('SELECT status FROM gate_entry_orders WHERE id=$1',[order.id])).rows[0].status,'consumed','booking invalidation must not revoke a used permission');
    assert.equal((await client.query('SELECT count(*)::int AS n FROM bookings')).rows[0].n,1,'no second business booking');
    // Migration is idempotent and never backfills permissions for old calls.
    await client.query(fs.readFileSync(path.join(__dirname,'../../database/migrations/20261018_018_dispatch_gate_entry_orders.sql'),'utf8'));
    assert.equal((await client.query("SELECT count(*)::int AS n FROM nexusport_schema_versions WHERE version='20261018_018'")).rows[0].n,1);
    console.log('PASS local SQL: call → pending permission → acknowledgment → 15-minute activation; reassignment revocation; expiry/requeue/notification; simulation does not create Gate In; consumption survives booking invalidation; migration retry; business booking retained.');
  }finally{await client.end();}
}
main().catch(error=>{console.error(error.message);process.exitCode=1;});

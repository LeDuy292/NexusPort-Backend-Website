'use strict';
// Shared DB verification is read-only; HTTP mutation cases are rejected before writes.
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const jwt=require('jsonwebtoken');
const {Client}=require('../../node-core/node_modules/pg');
require('dotenv').config({path:path.join(__dirname,'../../.env')});
async function main(){
  const c=new Client({connectionString:process.env.DATABASE_URL,connectionTimeoutMillis:15000});
  await c.connect();
  try{
    const configPath=path.join(__dirname,'../../csharp-core/src/NexusPort.Api/appsettings.json');
    const config=fs.existsSync(configPath)?JSON.parse(fs.readFileSync(configPath,'utf8').replace(/^\uFEFF/,'')):{};
    const secret=process.env.Jwt__Secret||config.Jwt?.Secret||'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!';
    const staff=(await c.query("SELECT id,role::text AS role FROM users WHERE is_active AND role::text IN ('Gate Officer','Administrator') ORDER BY CASE WHEN role::text='Gate Officer' THEN 0 ELSE 1 END LIMIT 1")).rows[0];
    assert.ok(staff,'Need an existing active gate/admin account for authenticated read checks.');
    const token=jwt.sign({sub:staff.id,id:staff.id,role:staff.role},secret,{expiresIn:300});
    const driver=jwt.sign({sub:staff.id,id:staff.id,role:'Driver'},secret,{expiresIn:300});
    const call=async(url,auth,body)=>{
      const response=await fetch('http://127.0.0.1:5000/api/v1/Gate/'+url,{method:body?'POST':'GET',headers:{...(auth?{Authorization:'Bearer '+auth}:{}),'Content-Type':'application/json'},...(body?{body:JSON.stringify(body)}:{}),signal:AbortSignal.timeout(20000)});
      return {status:response.status,data:await response.json().catch(()=>null)};
    };
    const checks=[];
    for(const endpoint of ['entry-orders','bookings?pageSize=150','barrier-commands?gateCode=GATE-01','verifications']){
      const result=await call(endpoint,token);
      assert.equal(result.status,200,endpoint+': '+JSON.stringify(result.data));
      checks.push({endpoint,status:result.status});
    }
    const queue=(await call('entry-orders',token)).data.items;
    if(queue.length){
      const order=queue[0];
      const calendar=await call('bookings?pageSize=150&search='+encodeURIComponent(order.bookingCode),token);
      assert.equal(calendar.status,200);
      assert.ok(calendar.data.items.some(booking=>booking.id===order.bookingId),'A dispatch order must resolve to the same booking in the gate calendar.');
      checks.push({endpoint:'dispatch order matches gate booking calendar',status:200});
    }
    assert.equal((await call('entry-orders')).status,401);
    assert.equal((await call('entry-orders',driver)).status,403);
    assert.equal((await call('bookings',driver)).status,403);
    const invalid=await call('barrier-commands',token,{requestId:'d7627de9-30ab-4e6a-8380-1d2f3f99b0bf',gateCode:'GATE-01',action:'open',source:'manual'});
    assert.ok([400,422].includes(invalid.status),'manual reason required: '+invalid.status);
    const missing=await call('barrier-commands',token,{requestId:'3a58230c-44a6-4a8d-9c20-012745e6ee66',gateCode:'GATE-01',action:'open',source:'automatic',verificationRecordId:'00000000-0000-4000-8000-000000000000'});
    assert.ok([400,422].includes(missing.status),'missing verification denied: '+missing.status+' '+JSON.stringify(missing.data));
    const gates=(await c.query('SELECT code FROM gates WHERE is_active ORDER BY code')).rows.map(x=>x.code);
    assert.deepEqual(gates,['GATE-01','GATE-03']);
    const map=(await c.query('SELECT map_data FROM smart_yard_maps WHERE is_active')).rows[0].map_data;
    assert.deepEqual(map.nodes.filter(n=>n.gateCode).map(n=>n.gateCode).sort(),gates);
    assert.equal((await c.query("SELECT count(*)::int AS n FROM gate_barrier_commands WHERE id IN ('d7627de9-30ab-4e6a-8380-1d2f3f99b0bf','3a58230c-44a6-4a8d-9c20-012745e6ee66')")).rows[0].n,0);
    console.log(JSON.stringify({checks,authorization:'401 anonymous / 403 Driver',deniedCommands:[invalid.status,missing.status],activeGates:gates,mapVersion:map.version,noTestCommandsPersisted:true}));
  }finally{await c.end();}
}
main().catch(error=>{console.error(error.message);process.exitCode=1});
